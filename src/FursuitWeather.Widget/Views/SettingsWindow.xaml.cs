using System.Globalization;
using System.Windows;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Time;
using FursuitWeather.Core.Update;
using FursuitWeather.Widget.Services;

namespace FursuitWeather.Widget.Views;

/// <summary>設定の画面。</summary>
/// <remarks>
/// <para>
/// いま実際に効く項目だけを置く。
/// 動かない飾りのコントロールを置くと、設定したのに効かないという誤解を生む。
/// </para>
/// <para>
/// 設計にある「計測中はインストールしない」は置かない。
/// 計測の機能がまだ無く、効かない飾りになるためである。
/// </para>
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly WidgetSettings _original;
    private readonly UpdateService? _updates;

    /// <summary>保存されたあとの設定。「やめる」で閉じたときは null。</summary>
    public WidgetSettings? Result { get; private set; }

    /// <summary>設定の画面を作る。</summary>
    /// <param name="settings">いまの設定。</param>
    /// <param name="notificationState">通知の状態を表す文。</param>
    /// <param name="updates">更新の仕組み。動いていなければ null で、更新の項目を出さない。</param>
    public SettingsWindow(WidgetSettings settings, string notificationState, UpdateService? updates)
    {
        ArgumentNullException.ThrowIfNull(settings);

        InitializeComponent();
        _original = settings;
        _updates = updates;

        // 768pxの画面でも保存を押せるよう、高さを作業領域に収める。はみ出た分は中身がスクロールする
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 40);

        LatitudeBox.Text = settings.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        LongitudeBox.Text = settings.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
        PlaceBox.Text = settings.PlaceName;
        StartInDisplayCheck.IsChecked = settings.StartInDisplay;

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

        if (_updates is null)
        {
            UpdateSection.Visibility = Visibility.Collapsed;
            UpdateStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var update = _updates.State;
        UpdateAutomaticRadio.IsChecked = update.Mode == UpdateMode.Automatic;
        UpdateDownloadOnlyRadio.IsChecked = update.Mode == UpdateMode.DownloadOnly;
        UpdateNotifyOnlyRadio.IsChecked = update.Mode == UpdateMode.NotifyOnly;
        PauseOnMeteredCheck.IsChecked = update.PauseOnMetered;
        PauseOnBatteryCheck.IsChecked = update.PauseOnBattery;

        // 開いているあいだに確認や取得が進んだら、その場で書き換える
        _updates.Changed += OnUpdatesChanged;
        Closed += (_, _) => _updates.Changed -= OnUpdatesChanged;
        RefreshUpdateStatus();
    }

    private void OnUpdatesChanged(object? sender, EventArgs e) => RefreshUpdateStatus();

    /// <summary>更新の状態を画面へ書く。</summary>
    private void RefreshUpdateStatus()
    {
        if (_updates is null)
        {
            return;
        }

        var state = _updates.State;
        RunningVersionText.Text = string.Create(CultureInfo.InvariantCulture, $"いまの版: {_updates.Running}");
        LastCheckedText.Text = state.LastCheckedAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"最後に確認した日時: {JstTime.ToLocal(at):yyyy-MM-dd HH:mm}")
            : "最後に確認した日時: まだ確認していません";

        // 取得を終えていれば、入れる場所まで案内する。設定画面からは入れない
        UpdateStatusText.Text = _updates.HasDownloadedUpdate
            ? string.Create(CultureInfo.InvariantCulture, $"{_updates.LastMessage}。トレイの「更新をインストール」から入れられます")
            : _updates.LastMessage;

        AutoDisabledText.Visibility = state.AutoUpdateDisabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_updates is null)
        {
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "確認しています…";
        try
        {
            // トレイの「更新を確認」と同じ流れを通る。保留なら大きさと理由を見せて尋ねる
            await _updates.CheckThenOfferDownloadAsync(
                question => MessageBox.Show(this, question, "FursuitWeather", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                .ConfigureAwait(true);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            RefreshUpdateStatus();
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
        var startupChanged = startup != StartupRegistration.IsEffectivelyEnabled();
        if (startupChanged)
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

        // 更新の扱いは、失敗しうる処理を済ませてから当てる。
        // 先に当てると、あとの失敗で画面を閉じずに「やめる」で閉じられたとき、扱いの変更だけが残る。
        // 当てられなければ、変えた自動起動を元へ戻してから止まる
        if (_updates is not null &&
            !_updates.ApplyPreferences(ReadUpdateMode(), PauseOnMeteredCheck.IsChecked == true, PauseOnBatteryCheck.IsChecked == true))
        {
            if (startupChanged && Environment.ProcessPath is { } revertPath)
            {
                StartupRegistration.Set(!startup, revertPath);
                StartupCheck.IsChecked = StartupRegistration.IsEffectivelyEnabled();
            }

            ShowError("更新の扱いを保存できませんでした。ファイルを書き込めない状態の可能性があります。");
            return;
        }

        Result = _original with
        {
            // 丸めた値を保存する。画面に出る値と、実際に送る値を一致させるため
            Latitude = coordinate.Latitude,
            Longitude = coordinate.Longitude,
            PlaceName = place,

            // 座標を入れ直して保存したときに立てる。
            // 保存しただけで立てると、地点に触れていない端末から
            // 「地点が設定されていません」の注意が消える
            LocationChosen = _original.HasChosenLocation || HasMovedLocation(coordinate),
            Layer = ReadLayer(),
            NotificationsEnabled = NotificationsCheck.IsChecked == true,
            StartWithWindows = startup,
            StartInDisplay = StartInDisplayCheck.IsChecked == true,
        };

        Result.Save();
        DialogResult = true;
    }

    /// <summary>座標が、開いたときの値から変わったか。</summary>
    /// <param name="coordinate">保存しようとしている座標。</param>
    /// <returns>変わっていれば true。</returns>
    private bool HasMovedLocation(Coordinate coordinate) =>
        Math.Abs(coordinate.Latitude - _original.Latitude) > double.Epsilon ||
        Math.Abs(coordinate.Longitude - _original.Longitude) > double.Epsilon;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private UpdateMode ReadUpdateMode()
    {
        if (UpdateAutomaticRadio.IsChecked == true)
        {
            return UpdateMode.Automatic;
        }

        return UpdateNotifyOnlyRadio.IsChecked == true ? UpdateMode.NotifyOnly : UpdateMode.DownloadOnly;
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
