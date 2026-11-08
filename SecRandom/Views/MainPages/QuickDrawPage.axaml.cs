using System;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SecRandom.Core.Abstraction;
using SecRandom.Core.Behaviors;
using SecRandom.Core.Abstraction.Services.Presentation;
using SecRandom.Core.Models.SubConfigs;
using SecRandom.Helpers;
using SecRandom.ViewModels.MainPages;
using CommonResources = SecRandom.Langs.Common.Resources;

namespace SecRandom.Views.MainPages;

public partial class QuickDrawPage : UserControl
{
    private bool _isUnloaded;
    private int _autoCloseRevision;
    private CancellationTokenSource? _autoCloseCts;
    private readonly ItemsControl? _resultPresenter;
    private IPointer? _dragPointer;
    private PixelPoint _dragStartScreenPoint;
    private PixelPoint _dragStartWindowPosition;
    private CancellationTokenSource? _clickSequenceCts;
    private int _clickCount;
    private int _remainingAutoCloseSeconds;
    private int _animationRevision;

    public QuickDrawPage()
    {
        ViewModel = IAppHost.GetService<QuickDrawPageViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
        _resultPresenter = this.FindControl<ItemsControl>("ResultPresenter");
        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        ViewModel.Config.EffectiveFloatingWindowSettings.PropertyChanged += FloatingWindowSettings_OnPropertyChanged;
        UpdateFloatingWindowOpacity();
        Unloaded += OnUnloaded;
    }

    public QuickDrawPageViewModel ViewModel { get; }

    /// <summary>
    /// 结果区完成一次排版（预览/揭晓/通知）后触发，宿主窗口据此按最新尺寸重新居中。
    /// 事件本身不含尺寸：窗口尺寸由宿主读取，避免页面耦合窗口实现。
    /// </summary>
    public event EventHandler? ResultLayoutChanged;

    public void StartDraw()
    {
        if (ViewModel.StartDrawCommand.CanExecute(null))
            ViewModel.StartDrawCommand.Execute(null);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        CancelAutoClose();
        CancelClickSequence();
        ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        ViewModel.Config.EffectiveFloatingWindowSettings.PropertyChanged -= FloatingWindowSettings_OnPropertyChanged;
    }

    private void FloatingWindowSettings_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FloatingWindowSettingsConfig.FloatingWindowOpacity))
            UpdateFloatingWindowOpacity();
    }

    private void UpdateFloatingWindowOpacity()
    {
        RootBorder.Opacity = System.Math.Clamp(
            ViewModel.NotificationOpacity ?? ViewModel.Config.EffectiveFloatingWindowSettings.FloatingWindowOpacity,
            20,
            100) / 100.0;
    }

    private void RootBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!App.SupportsProgrammaticWindowPositioning)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || TopLevel.GetTopLevel(this) is not Window window)
            return;

        _dragPointer = e.Pointer;
        _dragStartScreenPoint = window.Position + ToPixelPoint(e.GetPosition(window), window.RenderScaling);
        _dragStartWindowPosition = window.Position;
        e.Pointer.Capture(RootBorder);
    }

    private void RootBorder_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!App.SupportsProgrammaticWindowPositioning)
            return;
        if (!ReferenceEquals(e.Pointer, _dragPointer)
            || TopLevel.GetTopLevel(this) is not Window window)
            return;

        var screenPoint = window.Position + ToPixelPoint(e.GetPosition(window), window.RenderScaling);
        window.Position = new PixelPoint(
            _dragStartWindowPosition.X + screenPoint.X - _dragStartScreenPoint.X,
            _dragStartWindowPosition.Y + screenPoint.Y - _dragStartScreenPoint.Y);
    }

    private void RootBorder_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;

        if (ReferenceEquals(e.Pointer, _dragPointer))
        {
            _dragPointer = null;
            e.Pointer.Capture(null);
        }

        RegisterClick();
    }

    private static PixelPoint ToPixelPoint(Point point, double renderScaling)
    {
        return new PixelPoint(
            (int)Math.Round(point.X * renderScaling),
            (int)Math.Round(point.Y * renderScaling));
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUnloaded)
            return;

        if (e.PropertyName == nameof(QuickDrawPageViewModel.NotificationOpacity))
            UpdateFloatingWindowOpacity();

        Dispatcher.UIThread.Post(async () => await RunResultAnimationAsync(e.PropertyName), DispatcherPriority.Render);
    }

    private async Task RunResultAnimationAsync(string? propertyName)
    {
        if (_isUnloaded)
            return;

        // 预览帧按 AnimationInterval（最短 50ms）连发，而一次预览动画本身要跑满同样的时长。
        // 没有门禁时这些任务会并发改写同一批卡片的 Opacity 与 RenderTransform：
        // 前一帧还没收尾，后一帧已经在复位，画面就停在半透明（像被压暗）或整块卡片消失只剩底板。
        // revision 只允许最新一帧继续，旧帧在所有 await 之后自行退出。
        var revision = ++_animationRevision;

        try
        {
            if (propertyName == nameof(QuickDrawPageViewModel.PreviewAnimationRevision))
            {
                CancelAutoClose();
                _autoCloseRevision++;
                await WaitForResultPresenterLayoutAsync();
                if (_isUnloaded || revision != _animationRevision)
                    return;
                ResultPresentationBridge.RestoreHostResult(_resultPresenter);
                if (await TryPresentAsync(DrawPresentationPhase.Preview))
                    return;
                if (_isUnloaded || revision != _animationRevision)
                    return;
                await DrawAnimationHelper.PreviewAsync(
                    _resultPresenter,
                    ViewModel.AnimationStyle,
                    ViewModel.PreviewAnimationDuration);
            }
            else if (propertyName == nameof(QuickDrawPageViewModel.ResultAnimationRevision))
            {
                var autoCloseRevision = ++_autoCloseRevision;
                CancelAutoClose();
                await WaitForResultPresenterLayoutAsync();
                if (_isUnloaded || revision != _animationRevision || autoCloseRevision != _autoCloseRevision)
                    return;
                if (await TryPresentAsync(DrawPresentationPhase.Reveal))
                {
                    await CloseAfterDelayAsync(autoCloseRevision);
                    return;
                }

                await DrawAnimationHelper.RevealAsync(
                    _resultPresenter,
                    ViewModel.AnimationEnabled,
                    ViewModel.AnimationStyle,
                    ViewModel.AnimationDuration);
                if (_isUnloaded || revision != _animationRevision || autoCloseRevision != _autoCloseRevision)
                    return;
                await CloseAfterDelayAsync(autoCloseRevision);
            }
            else if (propertyName == nameof(QuickDrawPageViewModel.NotificationDisplayRevision))
            {
                var autoCloseRevision = ++_autoCloseRevision;
                CancelAutoClose();
                await WaitForResultPresenterLayoutAsync();
                if (_isUnloaded || revision != _animationRevision || autoCloseRevision != _autoCloseRevision)
                    return;
                if (await TryPresentAsync(DrawPresentationPhase.Reveal))
                {
                    await CloseAfterDelayAsync(autoCloseRevision);
                    return;
                }
                if (ViewModel.NotificationAnimationEnabled)
                    await DrawAnimationHelper.RevealAsync(
                        _resultPresenter,
                        true,
                        ViewModel.AnimationStyle,
                        ViewModel.AnimationDuration);
                if (_isUnloaded || revision != _animationRevision || autoCloseRevision != _autoCloseRevision)
                    return;
                await CloseAfterDelayAsync(autoCloseRevision);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            if (revision == _animationRevision)
                RaiseResultLayoutChanged();
        }
    }

    private void RaiseResultLayoutChanged()
    {
        ResultLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task<bool> TryPresentAsync(DrawPresentationPhase phase)
    {
        var students = ViewModel.LastResultStudents;
        return ResultPresentationBridge.TryPresentAsync(
            ResultPresentationBridge.BuildRequest(DrawPresentationChannel.QuickDraw, phase, students,
                listName: ViewModel.SelectedStudentListName, requestedCount: students.Count,
                pluginAnimationId: ViewModel.PluginAnimationId,
                displayTitles: ViewModel.LastResultDisplayTitles),
            _resultPresenter);
    }

    private static async Task WaitForResultPresenterLayoutAsync()
    {
        // 结果卡片是 items 容器，重建发生在渲染通道里：等一次 Loaded 就够拿到新卡片。
        // 原来两轮 Render 外加 2×16ms 延时，每帧多背 32ms，十二帧就排成"点下去几秒后才开始抽"。
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded).GetTask();
    }

    private async Task CloseAfterDelayAsync(int autoCloseRevision)
    {
        var seconds = ViewModel.ResultAutoCloseTime;
        _remainingAutoCloseSeconds = seconds;
        UpdateAutoCloseHint();
        if (seconds == 0)
            return;

        var cts = new CancellationTokenSource();
        _autoCloseCts = cts;
        try
        {
            while (_remainingAutoCloseSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                _remainingAutoCloseSeconds--;
                UpdateAutoCloseHint();
            }

            if (!_isUnloaded && autoCloseRevision == _autoCloseRevision && ReferenceEquals(_autoCloseCts, cts))
            {
                // 关闭前把动画残留清干净：窗口对象会被复用，留着半透明状态会让下一次显示时先闪一块暗色底板。
                DrawAnimationHelper.ResetAll(_resultPresenter);
                (TopLevel.GetTopLevel(this) as Window)?.Close();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_autoCloseCts, cts))
                _autoCloseCts = null;
            cts.Dispose();
        }
    }

    private void CancelAutoClose()
    {
        _autoCloseCts?.Cancel();
        _autoCloseCts = null;
        _remainingAutoCloseSeconds = 0;
        UpdateAutoCloseHint();
    }

    private void UpdateAutoCloseHint()
    {
        AutoCloseHintTextBlock.Text = _remainingAutoCloseSeconds > 0
            ? string.Format(
                CommonResources.ResourceManager.GetString("C_QuickDrawAutoCloseHint", CommonResources.Culture)
                ?? throw new InvalidOperationException("Quick draw auto-close localization is missing."),
                _remainingAutoCloseSeconds)
            : CommonResources.ResourceManager.GetString("C_QuickDrawManualCloseHint", CommonResources.Culture)
              ?? throw new InvalidOperationException("Quick draw manual-close localization is missing.");
    }

    private void RegisterClick()
    {
        var clickCount = ++_clickCount;
        if (clickCount == 1)
        {
            RestartClickSequenceTimeout();
            return;
        }

        if (clickCount == 3)
        {
            CancelClickSequence();
            (TopLevel.GetTopLevel(this) as Window)?.Close();
        }
    }

    private void RestartClickSequenceTimeout()
    {
        _clickSequenceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _clickSequenceCts = cts;
        _ = ResetClickSequenceAfterDelayAsync(cts);
    }

    private async Task ResetClickSequenceAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
            if (ReferenceEquals(_clickSequenceCts, cts))
                _clickCount = 0;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_clickSequenceCts, cts))
                _clickSequenceCts = null;
            cts.Dispose();
        }
    }

    private void CancelClickSequence()
    {
        _clickSequenceCts?.Cancel();
        _clickSequenceCts = null;
        _clickCount = 0;
    }
}
