using Microsoft.Win32;

namespace FursuitWeather.Widget.Services;

/// <summary>自動起動の状態。</summary>
public enum StartupState
{
    /// <summary>登録していない。</summary>
    NotRegistered,

    /// <summary>登録していて、有効。</summary>
    Enabled,

    /// <summary>
    /// 登録はしているが、Windowsの側で無効にされている。
    /// </summary>
    /// <remarks>
    /// 利用者が「設定」の「アプリ」の「スタートアップ」やタスクマネージャーから切った状態。
    /// Runキーの値は残るため、値の有無だけを見ると「有効」と誤って判断する。
    /// </remarks>
    DisabledByUser,
}

/// <summary>
/// Windowsへサインインしたときの自動起動を扱う。
/// </summary>
/// <remarks>
/// <para>
/// <c>HKCU</c> のRunキーを使う。
/// 管理者権限が要らず、設定アプリとタスクマネージャーの一覧に出るため、
/// 利用者が自分で止められる。
/// </para>
/// <para>
/// タスクスケジューラは採らない。
/// 「最上位の特権で実行」を付けると通知が動かなくなる。
/// </para>
/// <para>
/// <b>Runキーの値の有無だけを見てはいけない。</b>
/// 利用者がWindowsの側で無効にしても値は残り、
/// <c>Explorer\StartupApproved\Run</c> にフラグが書かれるだけである。
/// 実機で確認したところ、Runの値が残ったまま無効になっている例も、
/// Runの値が無いのに無効のフラグだけが残っている例もあった。
/// </para>
/// </remarks>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "FursuitWeather";

    /// <summary>いまの状態を調べる。</summary>
    /// <returns>登録の有無と、Windowsの側で無効にされているか。</returns>
    public static StartupState GetState()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is null)
            {
                return StartupState.NotRegistered;
            }

            return IsDisabledByUser() ? StartupState.DisabledByUser : StartupState.Enabled;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return StartupState.NotRegistered;
        }
    }

    /// <summary>
    /// 実際に自動で起動する状態か。
    /// </summary>
    /// <returns>有効なら true。</returns>
    public static bool IsEffectivelyEnabled() => GetState() == StartupState.Enabled;

    /// <summary>登録の有無を切り替える。</summary>
    /// <param name="enabled">登録するなら true。</param>
    /// <param name="executablePath">起動する実行ファイルの場所。</param>
    /// <returns>指示どおりにできたら true。</returns>
    /// <remarks>
    /// 有効にするときは、Windowsの側の無効のフラグも消す。
    /// フラグが残っていると、Runの値を書き直しても起動しない。
    /// </remarks>
    public static bool Set(bool enabled, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                // 空白を含むパスに備えて引用符で囲む
                key.SetValue(ValueName, $"\"{executablePath}\"");
                ClearDisabledFlag();
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 自動起動の登録を、無効のフラグごと消す。
    /// </summary>
    /// <remarks>
    /// アンインストールのときに使う。
    /// <see cref="Set"/> と違い実行ファイルの場所を要さないため、
    /// 消す側の経路で「自分の場所が分からないから消せない」が起こらない。
    /// フラグを残すと、入れ直したときに無効のまま始まる。
    /// </remarks>
    public static void Remove()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // 消せなくてもアンインストールは続ける
        }

        ClearDisabledFlag();
    }

    /// <summary>Windowsの側で無効にされているかを見る。</summary>
    /// <remarks>
    /// 値はバイト列で、先頭のバイトの最下位のビットが立っていると無効を表す。
    /// 有効なら 02、無効なら 03 が入る。
    /// </remarks>
    private static bool IsDisabledByUser()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flag && (flag[0] & 1) != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>無効のフラグを消す。</summary>
    private static void ClearDisabledFlag()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 })
            {
                approved.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // 消せなくても、Runの値の書き込み自体は成功している
        }
    }
}
