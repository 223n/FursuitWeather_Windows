using System.Globalization;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Changes;

/// <summary>
/// 判定の悪化を見つけ、出すべき通知を決める。
/// </summary>
/// <remarks>
/// <para>
/// 純粋な関数として書く。時計もファイルも触らないため、テストで固められる。
/// 条件と抑制の規則の根拠は <c>docs/notifications.md</c> にある。
/// </para>
/// <para>
/// 判定そのもの（暑さ指数の計算、レベルの判定、連続活動時間の算出）はAPIが行う。
/// ここでやるのは差分を見ることだけで、判定を複製しない。
/// </para>
/// <para>
/// <b>公式発表（T3）は差分ではない。</b>
/// 前回の値と比べて決めるものではなく、外部の事実そのものである。
/// そのため基準の信頼性や応答の新しさに関わらず評価する。
/// </para>
/// </remarks>
public static class ChangeDetector
{
    /// <summary>
    /// 前回の状態といまの予報を比べ、出すべき通知を決める。
    /// </summary>
    /// <param name="state">前回の状態。初回や読み込めなかったときは null。</param>
    /// <param name="forecast">いまの予報。</param>
    /// <param name="alertActive">公式の熱中症警戒アラートが出ているか。</param>
    /// <param name="locationKey">地点の識別子。座標の文字列などを渡す。</param>
    /// <param name="now">いまの時刻。</param>
    /// <param name="options">調整値。省略すると既定値を使う。</param>
    /// <returns>次に保存する状態と、出すべき通知。</returns>
    public static DetectionResult Evaluate(
        ChangeState? state,
        ForecastResponse forecast,
        bool alertActive,
        string locationKey,
        DateTimeOffset now,
        ChangeDetectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(locationKey);

        var opt = options ?? ChangeDetectorOptions.Default;
        var target = SelectTarget(forecast, now, opt.Lookahead);

        var sameLocation = state is not null &&
            string.Equals(state.LocationKey, locationKey, StringComparison.Ordinal);

        // 基準が信用できないときは張り直す。差分は見ないが、公式発表は別に見る
        var rebasing = state is null ||
            now - state.SavedAt > opt.StaleState ||
            !sameLocation;

        // 古い応答では基準を更新しない。改善を誤検知しないため
        var freshForecast = state is null || forecast.GeneratedAt > state.BaselineGeneratedAt;

        var candidates = new List<PendingNotification>();

        // ---- 悪化と回復。基準が信用でき、応答が新しいときだけ見る
        if (!rebasing && freshForecast && target is not null)
        {
            AddChangeCandidates(candidates, state!, target, forecast, locationKey, now, opt);
        }

        // ---- 公式発表。基準の状態にも応答の新しさにも縛られない
        if (alertActive && state?.AlertActive != true)
        {
            candidates.Add(BuildAlert(state, target, locationKey, now));
        }

        var baseState = rebasing
            ? ChangeState.Rebase(
                forecast,
                target,
                alertActive,
                locationKey,
                now,
                state?.History,
                // 地点が変われば、回復の基準にする値は引き継がない
                sameLocation ? state?.DiscontinuedSuitWbgt : null)
            : state!;

        var accepted = ApplySuppression(candidates, baseState, locationKey, now, opt);
        var next = BuildNextState(baseState, forecast, target, alertActive, accepted, now, opt, rebasing, freshForecast);

        return new DetectionResult(next, accepted);
    }

    /// <summary>
    /// これからの数時間のうち、もっとも厳しい時間を選ぶ。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="now">いまの時刻。</param>
    /// <param name="lookahead">何時間先まで見るか。</param>
    /// <returns>選んだ時間。見つからなければ null。</returns>
    /// <remarks>
    /// 連続活動時間がもっとも短い時間を採る。同じなら早いほうを採る。
    /// <c>hours</c> は欠測で歯抜けになるため、添字ではなく時刻の値で扱う。
    /// </remarks>
    public static HourForecast? SelectTarget(ForecastResponse forecast, DateTimeOffset now, TimeSpan lookahead)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        // いまが属する時間の頭から見る。10時30分なら10時の行も対象にする
        var localNow = JstTime.ToLocal(now);
        var fromLocal = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
        var from = JstTime.ToInstant(fromLocal);
        var to = now + lookahead;

        HourForecast? best = null;
        var bestInstant = DateTimeOffset.MaxValue;

        foreach (var hour in forecast.Hours)
        {
            var instant = JstTime.ToInstant(hour.Time);
            if (instant is null || instant.Value < from || instant.Value > to)
            {
                continue;
            }

            if (best is null ||
                hour.Outdoor.ActivityMinutes < best.Outdoor.ActivityMinutes ||
                (hour.Outdoor.ActivityMinutes == best.Outdoor.ActivityMinutes && instant.Value < bestInstant))
            {
                best = hour;
                bestInstant = instant.Value;
            }
        }

        return best;
    }

    /// <summary>悪化と回復の候補を積む。</summary>
    /// <remarks>
    /// <para>
    /// 着用中止級（T1）と短縮（T2）は同じ悪化を指すため、出すのは片方だけにする。
    /// ただし <c>else if</c> でT2を消してはいけない。
    /// T1が自分の予算（1日1回）で落ちたとき、T2へ落ちる道が無くなり、
    /// その日2回目の「活動できる状態から0分への転落」が完全に無音になるためである。
    /// </para>
    /// <para>
    /// 両方を候補として積み、抑制を通った先頭の1件だけを採る。
    /// </para>
    /// </remarks>
    private static void AddChangeCandidates(
        List<PendingNotification> candidates,
        ChangeState state,
        HourForecast target,
        ForecastResponse forecast,
        string locationKey,
        DateTimeOffset now,
        ChangeDetectorOptions options)
    {
        var current = target.Outdoor;

        // T1 着用中止級。
        // danger と coldDanger はどちらも 0分・grade 4 で、数値だけでは移り変わりを検出できない
        if (current.ActivityMinutes == 0 &&
            (state.LastMinutes > 0 || !string.Equals(state.LastLevel, current.Level, StringComparison.Ordinal)))
        {
            candidates.Add(Build(NotificationKind.DiscontinueWear, target, state, locationKey, urgent: true));
        }

        // T2 短縮
        if (current.ActivityMinutes < state.LastMinutes)
        {
            candidates.Add(Build(NotificationKind.Shortened, target, state, locationKey, urgent: false));
        }

        // R1 復帰。唯一の改善の通知。悪化の候補があるときは見ない
        if (candidates.Count == 0 &&
            state.LastMinutes == 0 &&
            current.ActivityMinutes > 0 &&
            IsRecoveryConfirmed(forecast, target, state, options))
        {
            candidates.Add(Build(NotificationKind.Recovery, target, state, locationKey, urgent: false));
        }
    }

    /// <summary>
    /// 回復を認めてよいかを見る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2つの条件をどちらも満たしたときだけ認める。
    /// </para>
    /// <list type="number">
    /// <item>活動できる状態が、連続して所定の時間続くこと</item>
    /// <item>着用中止を知らせたときより、補正後のWBGTが所定の幅だけ下がっていること</item>
    /// </list>
    /// <para>
    /// 基準にする値が無いときは、直前の0分だった時点の補正後のWBGTで代える。
    /// 「基準が無いから無条件で認める」としてはいけない。
    /// 状態を張り直したあとや初回の起動で、わずかな改善でも回復を知らせてしまう。
    /// </para>
    /// </remarks>
    private static bool IsRecoveryConfirmed(
        ForecastResponse forecast,
        HourForecast target,
        ChangeState state,
        ChangeDetectorOptions options)
    {
        // R1 は前回が0分のときしか通らないため、LastSuitWbgt は「0分だった時点の値」である
        var anchor = state.DiscontinuedSuitWbgt ?? state.LastSuitWbgt;
        if (target.Outdoor.SuitWbgt > anchor - options.RecoveryDeadbandCelsius)
        {
            return false;
        }

        var from = JstTime.ToInstant(target.Time);
        if (from is null)
        {
            return false;
        }

        var need = options.RecoveryDwellHours;
        if (need <= 1)
        {
            return true;
        }

        var until = from.Value.AddHours(need - 1);
        var seen = 0;

        foreach (var hour in forecast.Hours)
        {
            var instant = JstTime.ToInstant(hour.Time);
            if (instant is null || instant.Value < from.Value || instant.Value > until)
            {
                continue;
            }

            if (hour.Outdoor.ActivityMinutes <= 0)
            {
                return false;
            }

            seen++;
        }

        // 欠測で歯抜けになっている場合、続いている確証が無いので認めない
        return seen >= need;
    }

    /// <summary>抑制の規則を当て、出してよい通知だけを残す。</summary>
    private static List<PendingNotification> ApplySuppression(
        List<PendingNotification> candidates,
        ChangeState state,
        string locationKey,
        DateTimeOffset now,
        ChangeDetectorOptions options)
    {
        var today = JstTime.ToLocal(now).Date;
        var accepted = new List<PendingNotification>();
        var satisfiedGroups = new HashSet<string>(StringComparer.Ordinal);
        var suppressedByCap = false;

        // 上限に数えるのは、着用中止級・公式発表・打ち切りの告知を除いたもの
        var countedToday = state.History.Count(r =>
            !IsExemptFromDailyCap(r.Kind) &&
            JstTime.ToLocal(r.At).Date == today);

        foreach (var candidate in candidates)
        {
            // 公式発表は抑制の対象外。署名の照合より先に通す。
            // T3の署名は地点ごとにほぼ一定のため、照合より後ろに置くと
            // 2回目以降の発表が履歴に残るかぎり永久に落ちる
            if (candidate.Kind == NotificationKind.OfficialAlert)
            {
                accepted.Add(candidate);
                continue;
            }

            // 同じ悪化について既に1件出していれば、代わりのほうは出さない
            if (candidate.AlternativeGroup is { } group && satisfiedGroups.Contains(group))
            {
                continue;
            }

            // 同じ内容は再び出さない
            if (state.History.Any(r => string.Equals(r.Signature, candidate.Signature, StringComparison.Ordinal)))
            {
                continue;
            }

            var sameKindToday = state.History
                .Count(r => r.Kind == candidate.Kind && JstTime.ToLocal(r.At).Date == today);

            // 着用中止級と復帰は1日1回
            if (candidate.Kind is NotificationKind.DiscontinueWear or NotificationKind.Recovery &&
                sameKindToday > 0)
            {
                continue;
            }

            // 同じ種類は所定の間隔をあける
            var lastSameKind = state.History
                .Where(r => r.Kind == candidate.Kind)
                .Select(r => (DateTimeOffset?)r.At)
                .DefaultIfEmpty(null)
                .Max();

            if (lastSameKind is { } last && now - last < options.SameKindCooldown)
            {
                continue;
            }

            // 1日の上限
            if (!IsExemptFromDailyCap(candidate.Kind))
            {
                if (countedToday >= options.DailyCap)
                {
                    suppressedByCap = true;
                    continue;
                }

                countedToday++;
            }

            accepted.Add(candidate);
            if (candidate.AlternativeGroup is { } accepted_group)
            {
                satisfiedGroups.Add(accepted_group);
            }
        }

        // 上限で捨てたことを1日1回だけ知らせる。黙って止めるより安全である
        if (suppressedByCap &&
            !state.History.Any(r =>
                r.Kind == NotificationKind.DailyCapReached && JstTime.ToLocal(r.At).Date == today))
        {
            accepted.Add(new PendingNotification
            {
                Kind = NotificationKind.DailyCapReached,
                Signature = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{locationKey}|cap|{today:yyyy-MM-dd}"),
            });
        }

        return accepted;
    }

    /// <summary>1日の上限に数えない種類か。</summary>
    private static bool IsExemptFromDailyCap(NotificationKind kind) =>
        kind is NotificationKind.DiscontinueWear
            or NotificationKind.OfficialAlert
            or NotificationKind.DailyCapReached;

    /// <summary>次に保存する状態を組み立てる。</summary>
    private static ChangeState BuildNextState(
        ChangeState baseState,
        ForecastResponse forecast,
        HourForecast? target,
        bool alertActive,
        IReadOnlyList<PendingNotification> accepted,
        DateTimeOffset now,
        ChangeDetectorOptions options,
        bool rebasing,
        bool freshForecast)
    {
        var history = baseState.History.ToList();
        foreach (var n in accepted)
        {
            history.Add(new NotificationRecord(n.Kind, now, n.Signature));
        }

        if (history.Count > options.HistoryLimit)
        {
            history.RemoveRange(0, history.Count - options.HistoryLimit);
        }

        // 公式発表の状態は常に写す。解除を取り込まないと、次の発表で通知できなくなる
        var next = baseState with
        {
            SavedAt = now,
            AlertActive = alertActive,
            History = history,
        };

        // 差分を見なかったときは、比べる基準を動かさない
        if (rebasing || !freshForecast || target is null)
        {
            return next;
        }

        var current = target.Outdoor;
        var discontinued = accepted.Any(n => n.Kind == NotificationKind.DiscontinueWear)
            ? current.SuitWbgt
            : accepted.Any(n => n.Kind == NotificationKind.Recovery)
                ? null
                : baseState.DiscontinuedSuitWbgt;

        return next with
        {
            BaselineGeneratedAt = forecast.GeneratedAt,
            LastMinutes = current.ActivityMinutes,
            LastLevel = current.Level,
            LastSuitWbgt = current.SuitWbgt,
            DiscontinuedSuitWbgt = discontinued,
        };
    }

    private static PendingNotification BuildAlert(
        ChangeState? state,
        HourForecast? target,
        string locationKey,
        DateTimeOffset now)
    {
        var level = target?.Outdoor.Level ?? state?.LastLevel ?? string.Empty;
        var minutes = target?.Outdoor.ActivityMinutes ?? state?.LastMinutes ?? 0;

        return new PendingNotification
        {
            Kind = NotificationKind.OfficialAlert,
            Hour = target,
            // 日付を混ぜ、履歴の上でどの発表かを見分けられるようにする。
            // 抑制には使わない（署名の照合より先に通すため）
            Signature = string.Create(
                CultureInfo.InvariantCulture,
                $"{locationKey}|alert|{JstTime.ToLocal(now):yyyy-MM-dd}"),
            IsUrgent = true,
            CurrentLevel = level,
            CurrentMinutes = minutes,
            PreviousLevel = state?.LastLevel ?? string.Empty,
            PreviousMinutes = state?.LastMinutes ?? 0,
            IsCold = target?.Outdoor.LevelId.IsCold() ?? false,
        };
    }

    private static PendingNotification Build(
        NotificationKind kind,
        HourForecast hour,
        ChangeState state,
        string locationKey,
        bool urgent) => new()
        {
            Kind = kind,
            Hour = hour,
            PreviousMinutes = state.LastMinutes,
            CurrentMinutes = hour.Outdoor.ActivityMinutes,
            PreviousLevel = state.LastLevel,
            CurrentLevel = hour.Outdoor.Level,
            // 種類を含める。着用中止級と短縮が同じ署名にならないようにするため
            Signature = string.Create(
                CultureInfo.InvariantCulture,
                $"{locationKey}|{kind}|{hour.Time}|{state.LastMinutes}|{hour.Outdoor.ActivityMinutes}|{hour.Outdoor.Level}"),
            IsUrgent = urgent,
            IsCold = hour.Outdoor.LevelId.IsCold(),
            // 着用中止級と短縮は同じ悪化を指す。どちらか1件だけを出す
            AlternativeGroup = kind is NotificationKind.DiscontinueWear or NotificationKind.Shortened
                ? "degradation"
                : null,
        };
}
