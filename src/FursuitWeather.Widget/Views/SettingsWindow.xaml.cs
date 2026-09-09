using System.Globalization;
using System.Windows;
using FursuitWeather.Core.Api;
using FursuitWeather.Widget.Services;

namespace FursuitWeather.Widget.Views;

/// <summary>設定の画面。</summary>
/// <remarks>
/// いま実際に効く項目だけを置く。
/// 更新の3モードや先読みの幅は、対応する仕組みを繋いだときに足す。
/// 動かない飾りのコントロールを置くと、設定したのに効かないという誤解を生む。
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly WidgetSettings _original;

    /// <summary>保存されたあとの設定。「やめる」で閉じたときは null。</summary>
    public WidgetSettings? Result { get; private set; }

    /// <summary>設定の画面を作る。</summary>
    /// <param name="settings">いまの設定。</param>
    /// <param name="notificationState">通知の状態を表す文。</param>
    public SettingsWindow(WidgetSettings settings, string notificationState)
    {
        ArgumentNullException.ThrowIfNull(settings);

        InitializeComponent();
        _original = settings;

        LatitudeBox.Text = settings.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        LongitudeBox.Text = settings.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
        PlaceBox.Text = settings.PlaceName;

        LayerTopRadio.IsChecked = settings.Layer == WindowLayer.AlwaysOnTop;
        LayerNormalRadio.IsChecked = settings.Layer == WindowLayer.Normal;
        LayerTrayRadio.IsChecked = settings.Layer == WindowLayer.TrayOnly;

        NotificationsCheck.IsChecked = settings.NotificationsEnabled;
        NotificationStateText.Text = $"いまの状態: {notificationState}";

        // 実際に起動する状態かを正とする。
        // Runキーの値の有無だけを見ると、Windowsの側で無効にされていても「有効」に見える
        var startupState = StartupRegistration.GetState();
        StartupCheck.IsChecked = startupState == StartupState.Enabled;

        if (startupState == StartupState.DisabledByUser)
        {
            StartupStateText.Text = "Windowsの側で無効にされています。ここで入れ直すと有効に戻ります。";
            StartupStateText.Visibility = Visibility.Visible;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryReadCoordinate(out var coordinate, out var message))
        {
            ShowError(message);
            return;
        }

        var place = PlaceBox.Text.Trim();
        if (place.Length == 0)
        {
            ShowError("表示名を入れてください。");
            return;
        }

        if (place.Length > 40)
        {
            ShowError("表示名は40文字までにしてください。");
            return;
        }

        var startup = StartupCheck.IsChecked == true;
        if (startup != StartupRegistration.IsEffectivelyEnabled())
        {
            // 座標や表示名の検証と同じく、失敗したら閉じない。
            // 閉じてしまうと、変えられなかったことが利用者に伝わらない
            var path = Environment.ProcessPath;
            if (path is null)
            {
                ShowError("実行ファイルの場所が分からないため、自動起動を変えられませんでした。");
                StartupCheck.IsChecked = StartupRegistration.IsEffectivelyEnabled();
                return;
            }

            if (!StartupRegistration.Set(startup, path))
            {
                ShowError("自動起動を変えられませんでした。端末の設定やセキュリティ製品が止めている可能性があります。チェックはいまの状態へ戻しました。");
                StartupCheck.IsChecked = StartupRegistration.IsEffectivelyEnabled();
                return;
            }
        }

        Result = _original with
        {
            // 丸めた値を保存する。画面に出る値と、実際に送る値を一致させるため
            Latitude = coordinate.Latitude,
            Longitude = coordinate.Longitude,
            PlaceName = place,
            Layer = ReadLayer(),
            NotificationsEnabled = NotificationsCheck.IsChecked == true,
            StartWithWindows = startup,
        };

        Result.Save();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private WindowLayer ReadLayer()
    {
        if (LayerTrayRadio.IsChecked == true)
        {
            return WindowLayer.TrayOnly;
        }

        return LayerNormalRadio.IsChecked == true ? WindowLayer.Normal : WindowLayer.AlwaysOnTop;
    }

    private bool TryReadCoordinate(out Coordinate coordinate, out string message)
    {
        coordinate = default;
        message = string.Empty;

        if (!double.TryParse(LatitudeBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
        {
            message = "緯度を数字で入れてください。";
            return false;
        }

        if (!double.TryParse(LongitudeBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            message = "経度を数字で入れてください。";
            return false;
        }

        try
        {
            coordinate = new Coordinate(lat, lon);
            return true;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            message = ex.Message.Split('(')[0].Trim();
            return false;
        }
    }
}
