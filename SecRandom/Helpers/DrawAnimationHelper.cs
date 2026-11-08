using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SecRandom.Core.Behaviors;
using SecRandom.Core.Enums.Configs;

namespace SecRandom.Helpers;

internal static class DrawAnimationHelper
{
    private const string ResultAnimationClass = "result-animation-item";

    // 入场不再从全透明起步：0 起始会让卡片先以「只有底板」的形态存在若干帧（肉眼看到白框）。
    private const double PreviewStartOpacity = 0.55;
    private const double RevealStartOpacity = 0.35;
    private static readonly Dictionary<Control, CancellationTokenSource> AnimationTokens = [];

    public static async Task PreviewAsync(Control? target, DrawAnimationStyleMode style, int durationMs)
    {
        if (target is null)
            return;

        // 帧间隔由调用方控制（默认 50ms），上一帧的卡片早就量好了。
        // 这里只在容器确实还没布局时等一次，否则每帧多背一次 layout 往返，
        // 12 帧就会排队成"点下去几秒后才开始动"。
        if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
            await EnsureLayoutAsync(target).ConfigureAwait(true);

        var resultItems = GetResultAnimationItems(target).ToList();
        if (style == DrawAnimationStyleMode.DirectRotate || durationMs <= 0)
        {
            ResetTarget(target);
            foreach (var item in resultItems)
                ResetTarget(item);
            return;
        }

        // 卡片容器还没生成（ItemsControl 尚未测量）时什么都不做：
        // 对整块面板做 0→1 淡入只会在下一帧留下一块半透明底板，看起来就是「透明度莫名降低」。
        if (resultItems.Count == 0)
            return;

        var tasks = resultItems.Select(item => AnimateAsync(item, style, durationMs, 0, PreviewStartOpacity));
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    public static async Task RevealAsync(Control? target, bool enabled, DrawAnimationStyleMode style, int durationMs)
    {
        if (target is null)
            return;

        // 揭晓是这一轮的最后一次写入：先把预览阶段可能残留的半透明/位移清干净，
        // 否则上一帧被抢占后停在 opacity≈0 的卡片会直接变成一块空白面板。
        ResetAll(target);

        if (!enabled || style == DrawAnimationStyleMode.DirectRotate || durationMs <= 0)
            return;

        await EnsureLayoutAsync(target).ConfigureAwait(true);
        var resultItems = GetResultAnimationItems(target).ToList();

        if (resultItems.Count == 0)
        {
            await AnimateAsync(target, style, durationMs).ConfigureAwait(true);
            return;
        }

        var tasks = resultItems.Select((item, index) => AnimateAsync(
            item,
            style,
            durationMs,
            Math.Min(index * 45, 220),
            RevealStartOpacity));
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 窗口在这一轮动画中途关闭或又来了新一轮，收尾交给 finally。
        }
        finally
        {
            // 终态兜底：无论动画是否被中途取消，结果卡片都必须停在完全可见的状态。
            ResetAll(target);
        }
    }

    /// <summary>取消目标上所有在跑的动画，但不改变它们当前的视觉状态。</summary>
    public static void CancelAll(Control? target)
    {
        if (target is null)
            return;

        Cancel(target);
        foreach (var item in GetResultAnimationItems(target).ToList())
            Cancel(item);
    }

    /// <summary>取消并复位目标与所有结果卡片，清除半透明、位移与旋转残留。</summary>
    public static void ResetAll(Control? target)
    {
        if (target is null)
            return;

        ResetTarget(target);
        foreach (var item in GetResultAnimationItems(target).ToList())
            ResetTarget(item);
    }

    private static async Task EnsureLayoutAsync(Control target)
    {
        await Dispatcher.UIThread.InvokeAsync(target.UpdateLayout, DispatcherPriority.Render).GetTask()
            .ConfigureAwait(true);
        await Task.Delay(AnimationFramePolicy.FrameIntervalMs).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(target.UpdateLayout, DispatcherPriority.Render).GetTask()
            .ConfigureAwait(true);
    }

    private static async Task AnimateAsync(
        Control target,
        DrawAnimationStyleMode style,
        int durationMs,
        int delayMs = 0,
        double startOpacity = 1)
    {
        var source = ReplaceToken(target);
        var token = source.Token;
        var transformGroup = BuildTransformGroup(style);
        try
        {
            await ApplyAnimationFrameAsync(target, transformGroup, style, 0, startOpacity).ConfigureAwait(true);

            if (delayMs > 0)
                await Task.Delay(delayMs, token).ConfigureAwait(true);

            var steps = AnimationFramePolicy.ResolveFrameCount(durationMs);
            for (var i = 0; i <= steps; i++)
            {
                token.ThrowIfCancellationRequested();
                var progress = (double)i / steps;
                await ApplyAnimationFrameAsync(target, transformGroup, style, progress, startOpacity)
                    .ConfigureAwait(true);
                await Task.Delay(AnimationFramePolicy.ResolveFrameDelayMs(durationMs, steps), token)
                    .ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // 被更新的预览帧或揭晓抢占：视觉状态已由 ReplaceToken 复位，这里直接退出。
            return;
        }
        finally
        {
            ReleaseToken(target, source);
        }

        ApplyNeutralState(target);
    }

    private static Task ApplyAnimationFrameAsync(
        Control target,
        TransformGroup transformGroup,
        DrawAnimationStyleMode style,
        double progress,
        double startOpacity)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            var eased = EaseOut(progress);
            target.RenderTransformOrigin = RelativePoint.Center;
            target.RenderTransform = transformGroup;
            target.Opacity = ResolveOpacity(style, progress, eased, startOpacity);

            if (transformGroup.Children[0] is TranslateTransform translate)
            {
                translate.X = ResolveTranslateX(style, progress, eased);
                translate.Y = ResolveTranslateY(style, eased);
            }

            if (transformGroup.Children[1] is ScaleTransform scale)
            {
                var scaleValue = ResolveScale(style, progress, eased);
                scale.ScaleX = scaleValue;
                scale.ScaleY = scaleValue;
            }

            if (transformGroup.Children[2] is RotateTransform rotate)
                rotate.Angle = ResolveAngle(style, progress, eased);
        }, DispatcherPriority.Render).GetTask();
    }

    private static IEnumerable<Control> GetResultAnimationItems(Control target)
    {
        return target.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.Classes.Contains(ResultAnimationClass));
    }

    private static double EaseOut(double progress)
    {
        return 1 - Math.Pow(1 - progress, 3);
    }

    private static TransformGroup BuildTransformGroup(DrawAnimationStyleMode style)
    {
        var (offsetY, scale, angle) = style switch
        {
            DrawAnimationStyleMode.FadeFloat => (34.0, 0.96, 0.0),
            DrawAnimationStyleMode.HorizontalShake => (4.0, 1.0, 0.0),
            _ => (0.0, 1.0, 0.0)
        };

        return new TransformGroup
        {
            Children =
            [
                new TranslateTransform(ResolveTranslateX(style, 0, 0), offsetY),
                new ScaleTransform(scale, scale),
                new RotateTransform(angle)
            ]
        };
    }

    private static double ResolveOpacity(DrawAnimationStyleMode style, double progress, double eased)
    {
        return ResolveOpacity(style, progress, eased, ResolveStartOpacity(style));
    }

    private static double ResolveOpacity(DrawAnimationStyleMode style, double progress, double eased, double startOpacity)
    {
        return startOpacity + (1 - startOpacity) * eased;
    }

    private static double ResolveStartOpacity(DrawAnimationStyleMode style)
    {
        return 0;
    }

    private static double ResolveTranslateX(DrawAnimationStyleMode style, double progress, double eased)
    {
        return style switch
        {
            DrawAnimationStyleMode.HorizontalShake => Math.Sin(progress * Math.PI * 6 - Math.PI / 2) * (1 - eased) * 34,
            _ => 0
        };
    }

    private static double ResolveTranslateY(DrawAnimationStyleMode style, double eased)
    {
        return style == DrawAnimationStyleMode.FadeFloat ? (1 - eased) * 34 : 0;
    }

    private static double ResolveScale(DrawAnimationStyleMode style, double progress, double eased)
    {
        return style switch
        {
            DrawAnimationStyleMode.FadeFloat => 0.96 + 0.04 * eased,
            DrawAnimationStyleMode.HorizontalShake => 0.98 + 0.02 * eased,
            _ => 1
        };
    }

    private static double ResolveAngle(DrawAnimationStyleMode style, double progress, double eased)
    {
        return style == DrawAnimationStyleMode.HorizontalShake
            ? Math.Sin(progress * Math.PI * 6 - Math.PI / 2) * (1 - eased) * 3
            : 0;
    }

    private static void ResetTarget(Control target)
    {
        Cancel(target);
        ApplyNeutralState(target);
    }

    /// <summary>只把视觉属性写回中性态，不动任何令牌。</summary>
    private static void ApplyNeutralState(Control target)
    {
        target.Opacity = 1;
        target.RenderTransformOrigin = RelativePoint.Center;
        target.RenderTransform = null;
    }

    private static CancellationTokenSource ReplaceToken(Control target)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        lock (AnimationTokens)
        {
            AnimationTokens.TryGetValue(target, out previous);
            AnimationTokens.Remove(target);
            current = new CancellationTokenSource();
            AnimationTokens[target] = current;
        }

        if (previous is null)
            return current;

        // 只取消、绝不 Dispose：旧动画可能正停在 Task.Delay 上，
        // Dispose 一个仍被 await 的 token 会抛 ObjectDisposedException，
        // 从 catch(OperationCanceledException) 直接逃逸，动画就僵在半透明状态。
        previous.Cancel();

        // 取消即复位。把"清干净"寄希望于继任者第一帧并不可靠：继任者要经过一次
        // Dispatcher 往返才写值，中间那一帧就是用户看到的半透明残留。
        // 这里只改视觉属性、不碰 token（当前 token 已经换成新的那个）。
        ApplyNeutralState(target);

        return current;
    }

    private static void ReleaseToken(Control target, CancellationTokenSource source)
    {
        lock (AnimationTokens)
        {
            if (AnimationTokens.TryGetValue(target, out var current) && ReferenceEquals(current, source))
                AnimationTokens.Remove(target);
        }

        source.Dispose();
    }

    private static void Cancel(Control target)
    {
        CancellationTokenSource? current;
        lock (AnimationTokens)
        {
            if (!AnimationTokens.Remove(target, out current))
                return;
        }

        current?.Cancel();
    }
}
