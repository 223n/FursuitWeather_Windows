namespace FursuitWeather.Widget.Services;

/// <summary>小窓をどの高さに置くか。</summary>
/// <remarks>
/// 最背面への固定は用意しない。
/// 実測の結果、ほかの窓に隠れて見えないうえ、「デスクトップの表示」でも
/// 見えなくなると分かったためである。
/// 詳しくは <c>docs/open-questions.md</c> の「小窓の高さ」にある。
/// </remarks>
public enum WindowLayer
{
    /// <summary>常に手前。既定。</summary>
    AlwaysOnTop,

    /// <summary>ほかの窓に隠れてよい。配信や画面の共有に映したくないときに選ぶ。</summary>
    Normal,

    /// <summary>小窓を出さず、トレイだけにする。</summary>
    TrayOnly,
}
