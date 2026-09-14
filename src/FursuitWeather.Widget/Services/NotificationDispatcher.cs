using System.Globalization;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Briefing;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Notifications;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Widget.Services;

/// <summary>通知を出そうとした結果。</summary>
/// <param name="Message">出そうとした内容。</param>
/// <param name="At">出そうとした時刻。</param>
/// <param name="Shown">トーストとして出せたか。</param>
/// <param name="Suppressed">掲示中のため、出さずに止めたか。</param>
public sealed record NotificationAttempt(
    NotificationMessage Message,
    DateTimeOffset At,
    bool Shown,
    bool Suppressed = false);

/// <summary>
/// 取得した予報から通知を出し、判断に使う状態を保存する。
/// </summary>
/// <remarks>
/// <para>
/// 何を出すかは <see cref="NotificationPlanner"/> が決める。
/// ここが持つのは、状態の読み書きと、実際に出す手段だけである。
/// </para>
/// <para>
/// <b>トーストが出せなかったときは <see cref="FellBack"/> で外へ渡す。</b>
/// 通知だけが静かに壊れる状態を作らないためである。
/// 受け取った側は小窓とトレイへ倒すこと。
/// </para>
/// </remarks>
public sealed class NotificationDispatcher
{
    /// <summary>診断のために覚えておく件数。</summary>
    private const int AttemptLimit = 5;

    private readonly ToastNotifier _toast;
    private readonly List<NotificationAttempt> _attempts = [];
    private NotificationState _state = NotificationStateStore.Load();
    private string _lastOutcome = "まだ1回も判定していません";

    /// <summary>トーストとして出せなかったときに起きる。</summary>
    public event EventHandler<NotificationMessage>? FellBack;

    /// <summary>通知を出す設定になっているか。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 掲示中のため、組み上がった文面を出さずに止めるか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>判定と保存は止めない。</b>
    /// 掲示が同じ判定を画面に出し続けているため、状態は掲示の外と同じように進める
    /// （<c>docs/display.md</c>）。
    /// </para>
    /// <para>
    /// 止めたぶんは「出せなかった通知」として扱わない。
    /// 小窓とトレイのバルーンへ倒すと、来場者の見る画面にバルーンが出る。
    /// </para>
    /// <para>
    /// 設定で通知を切っているときは、そちらが優先される。判定も保存も行わない。
    /// </para>
    /// </remarks>
    public bool Suppressed { get; set; }

    /// <summary>直近に出そうとした通知。新しいものが後ろに来る。</summary>
    public IReadOnlyList<NotificationAttempt> Attempts => _attempts;

    /// <summary>取得と通知を作る。</summary>
    /// <param name="toast">通知を出す手段。</param>
    public NotificationDispatcher(ToastNotifier toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        _toast = toast;
    }

    /// <summary>
    /// 取得した内容から通知を出す。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="alert">公式の発表。無ければ null。</param>
    /// <param name="coordinate">地点。丸めたあとの座標を識別子に使う。</param>
    /// <param name="placeName">地点の表示名。</param>
    /// <param name="now">いまの時刻。</param>
    /// <remarks>
    /// <para>
    /// 設定で通知を切っているときは、判定もせず保存もしない。
    /// 切っているあいだに基準だけを進めると、入れ直した直後に
    /// 「切っているあいだに起きた悪化」を取りこぼす。
    /// </para>
    /// <para>
    /// 基準は据え置かれるため、6時間以内に入れ直せばその悪化はそこで通知される。
    /// 6時間を越えていれば古い基準として張り直され、そこは黙る。
    /// </para>
    /// </remarks>
    public void Process(
        ForecastResponse forecast,
        HeatAlert? alert,
        Coordinate coordinate,
        string placeName,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        if (!Enabled)
        {
            _lastOutcome = "設定で通知を切っているため、判定していません";
            return;
        }

        var plan = NotificationPlanner.Create(
            _state,
            forecast,
            alert,
            coordinate.ToString(),
            placeName,
            now);

        _state = plan.State;

        // 出せたかに関わらず保存する。
        // 出せなかったぶんを保存せずに戻すと、壊れているあいだ11分ごとに同じ通知を出し続ける。
        // 届かなかったぶんは FellBack で小窓とトレイへ倒す
        NotificationStateStore.Save(_state);

        if (plan.Messages.Count == 0)
        {
            _lastOutcome = string.Create(
                CultureInfo.InvariantCulture,
                $"{JstTime.ToLocal(now):H時m分} に判定しました。出すものはありませんでした");
            return;
        }

        if (Suppressed)
        {
            // 出さずに止める。履歴には残るため、1日の上限と同種の間隔には数えられる
            foreach (var message in plan.Messages)
            {
                Remember(new NotificationAttempt(message, now, Shown: false, Suppressed: true));
            }

            _lastOutcome = string.Create(
                CultureInfo.InvariantCulture,
                $"{JstTime.ToLocal(now):H時m分} に{plan.Messages.Count}件（掲示中のため出していません）");
            return;
        }

        var shownCount = Deliver(plan.Messages, now);

        _lastOutcome = string.Create(
            CultureInfo.InvariantCulture,
            $"{JstTime.ToLocal(now):H時m分} に{plan.Messages.Count}件（トーストで{shownCount}件）");
    }

    /// <summary>
    /// いまの予報で悪化が起きたとみなし、出るはずの文面を組み立てる。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="alert">公式の発表。無ければ null。</param>
    /// <param name="coordinate">地点。</param>
    /// <param name="placeName">地点の表示名。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>組み上がった文面。</returns>
    /// <remarks>
    /// <para>
    /// 保存してある状態には触らない。基準も履歴も動かさない。
    /// <c>static</c> にしてあるのは、触れないことをコンパイラに保証させるためである。
    /// </para>
    /// <para>
    /// 通知は、判定が実際に悪化するまで一度も出ない。
    /// 出るまで何日も待たないと配線の正しさを確かめられないため、
    /// 「45分・ほぼ安全から、いまの実データへ変わった」という仮の基準を当てる。
    /// 予報そのものは本物であり、文面に出る値も本物である。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<NotificationMessage> Preview(
        ForecastResponse forecast,
        HeatAlert? alert,
        Coordinate coordinate,
        string placeName,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        var baseline = new ChangeState
        {
            SavedAt = now - TimeSpan.FromMinutes(10),
            LocationKey = coordinate.ToString(),

            // 応答が新しいと見なされないと、悪化の判定そのものが走らない
            BaselineGeneratedAt = forecast.GeneratedAt - TimeSpan.FromHours(1),
            LastMinutes = 45,
            LastLevel = "safe",
            LastLabel = "ほぼ安全",
            LastSuitWbgt = 0d,

            // 公式発表は「前回は出ていなかった」から見る。出ていれば文面が組まれる
            AlertActive = false,
        };

        return NotificationPlanner.Create(
            new NotificationState { Change = baseline },
            forecast,
            alert,
            coordinate.ToString(),
            placeName,
            now,

            // 朝のブリーフィングは時刻の条件で落ちる。試すときは時刻に縛られないようにする
            briefingOptions: new BriefingOptions
            {
                DeliverAt = TimeOnly.MinValue,
                LatestDeliveryAt = TimeOnly.MaxValue,
            }).Messages;
    }

    /// <summary>
    /// 組み立てた文面を、実際に出す。
    /// </summary>
    /// <param name="messages">出す文面。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>トーストとして出せた件数。</returns>
    /// <remarks>
    /// 出せなかったものは <see cref="FellBack"/> で外へ渡す。
    /// 動作を確かめる経路でも、本番と同じ落とし方を通す。
    /// </remarks>
    public int Deliver(IReadOnlyList<NotificationMessage> messages, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var shownCount = 0;
        foreach (var message in messages)
        {
            var shown = _toast.Show(message.Title, message.Lines, message.IsUrgent);
            Remember(new NotificationAttempt(message, now, shown));

            if (shown)
            {
                shownCount++;
            }
            else
            {
                FellBack?.Invoke(this, message);
            }
        }

        return shownCount;
    }

    /// <summary>
    /// いまの状態と直近の結果を、人が読める形で返す。
    /// </summary>
    /// <param name="now">いまの時刻。</param>
    /// <returns>診断の画面へ出す文字列。</returns>
    /// <remarks>
    /// 通知は出ないことが正しい時間のほうが長い。
    /// 動いているのか壊れているのかを、出ていないあいだにも確かめられるようにする。
    /// </remarks>
    public string Describe(DateTimeOffset now)
    {
        var lines = new List<string>
        {
            NotificationPlanner.Describe(_state, now),
            $"直近の判定: {_lastOutcome}",
        };

        if (_attempts.Count > 0)
        {
            lines.Add("直近に出そうとした通知:");
            foreach (var attempt in _attempts.AsEnumerable().Reverse())
            {
                var how = attempt.Suppressed ? "掲示中で出さず" : attempt.Shown ? "トースト" : "出せず";
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {JstTime.ToLocal(attempt.At):M/d H:mm} [{how}] {attempt.Message.Describe()}"));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void Remember(NotificationAttempt attempt)
    {
        _attempts.Add(attempt);
        if (_attempts.Count > AttemptLimit)
        {
            _attempts.RemoveRange(0, _attempts.Count - AttemptLimit);
        }
    }
}
