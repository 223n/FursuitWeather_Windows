using System.Runtime.InteropServices;

namespace FursuitWeather.Widget.Interop;

/// <summary>
/// 掲示のあいだ、画面と端末を眠らせない。
/// </summary>
/// <remarks>
/// <para>
/// <b>スクリーンセーバーは止められない。</b>
/// この関数はスクリーンセーバーの起動を止めない、とMicrosoftの文書に明記されている。
/// 画面のロックが止まるかも確かめていない。
/// どちらも設置の手順で切ってもらう（<c>docs/display.md</c>）。
/// </para>
/// <para>
/// 要求はスレッドごとに持つ。掲示を始めたスレッドと同じスレッドで解くこと。
/// </para>
/// </remarks>
internal static partial class DisplaySleep
{
    private const uint EsContinuous = 0x8000_0000;
    private const uint EsDisplayRequired = 0x0000_0002;
    private const uint EsSystemRequired = 0x0000_0001;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint esFlags);

    /// <summary>眠らせない状態にする。</summary>
    /// <returns>要求できたら true。</returns>
    public static bool Keep() =>
        SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired) != 0;

    /// <summary>元へ戻す。</summary>
    public static void Release() => _ = SetThreadExecutionState(EsContinuous);
}
