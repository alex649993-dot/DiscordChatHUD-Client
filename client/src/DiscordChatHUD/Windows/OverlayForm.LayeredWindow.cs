using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// OverlayForm — 투명(레이어드) 창 표면 갱신.
internal sealed partial class OverlayForm
{

    private bool _layeredSurfaceNeedsFullCopy = true;
    private bool _partialPresentationUnavailable;
    private Bitmap? _lastPresentedBitmap;
    private IntPtr _lastPresentedWindow;
    internal bool PartialPresentationEnabled = true;
    internal readonly record struct LayeredPresentationResult(long Bytes, bool Partial, bool Submitted);

    private unsafe LayeredPresentationResult ApplyLayeredBitmap(Bitmap bitmap, Rectangle? dirtyBounds = null)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) { _layeredSurfaceNeedsFullCopy = true; return default; }
        try
        {
            EnsureLayeredSurface(screenDc, bitmap.Size);
            if (_surfaceBits == IntPtr.Zero || _surfaceDc == IntPtr.Zero)
            { _layeredSurfaceNeedsFullCopy = true; return default; }
            var window = Handle;
            var fullBounds = new Rectangle(Point.Empty, bitmap.Size);
            var partial = PartialPresentationEnabled && !_partialPresentationUnavailable
                && !_layeredSurfaceNeedsFullCopy && ReferenceEquals(_lastPresentedBitmap, bitmap)
                && _lastPresentedWindow == window && dirtyBounds.HasValue;
            var bounds = partial ? Rectangle.Intersect(fullBounds, dirtyBounds!.Value) : fullBounds;
            if (partial && (bounds.Width <= 0 || bounds.Height <= 0)) return default;
            partial &= bounds != fullBounds;
            var copied = LayeredBitmapCopy.Copy(bitmap, _surfaceBits, bitmap.Width * 4, bounds);
            var destination = new NativeMethods.Point(Left, Top);
            var size = new NativeMethods.Size(bitmap.Width, bitmap.Height);
            var source = new NativeMethods.Point(0, 0);
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.AcSrcOver, SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AcSrcAlpha
            };
            bool updated;
            if (partial)
            {
                var dirty = new NativeMethods.Rect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
                var info = new NativeMethods.UpdateLayeredWindowInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.UpdateLayeredWindowInfo>(),
                    DestinationDc = screenDc, DestinationPoint = (IntPtr)(&destination), WindowSize = (IntPtr)(&size),
                    SourceDc = _surfaceDc, SourcePoint = (IntPtr)(&source), Blend = (IntPtr)(&blend),
                    Flags = NativeMethods.UlwAlpha, DirtyRect = (IntPtr)(&dirty)
                };
                updated = NativeMethods.UpdateLayeredWindowIndirect(window, ref info);
                if (!updated)
                {
                    var error = Marshal.GetLastWin32Error();
                    _partialPresentationUnavailable = true;
                    // Restore the complete surface before retrying the established API.
                    copied += LayeredBitmapCopy.Copy(bitmap, _surfaceBits, bitmap.Width * 4, fullBounds);
                    partial = false;
                    AppLog.Warn($"부분 화면 출력 대신 전체 출력 사용: Win32 {error}");
                    updated = NativeMethods.UpdateLayeredWindow(window, screenDc, ref destination, ref size,
                        _surfaceDc, ref source, 0, ref blend, NativeMethods.UlwAlpha);
                }
            }
            else updated = NativeMethods.UpdateLayeredWindow(window, screenDc, ref destination, ref size,
                _surfaceDc, ref source, 0, ref blend, NativeMethods.UlwAlpha);
            _layeredSurfaceNeedsFullCopy = !updated;
            if (updated) { _lastPresentedBitmap = bitmap; _lastPresentedWindow = window; }
            else AppLog.Warn($"UpdateLayeredWindow 호출 실패: Win32 {Marshal.GetLastWin32Error()}");
            return new(copied, partial, updated);
        }
        catch { _layeredSurfaceNeedsFullCopy = true; throw; }
        finally { NativeMethods.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    private void EnsureLayeredSurface(IntPtr screenDc, Size size)
    {
        if (_surfaceDc != IntPtr.Zero && _surfaceBitmap != IntPtr.Zero && _surfaceBits != IntPtr.Zero
            && _surfaceSize == size) return;
        DestroyLayeredSurface();
        _surfaceDc = NativeMethods.CreateCompatibleDC(screenDc);
        if (_surfaceDc == IntPtr.Zero) return;
        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                Width = size.Width,
                Height = -size.Height,
                Planes = 1,
                BitCount = 32,
                Compression = NativeMethods.BiRgb
            }
        };
        _surfaceBitmap = NativeMethods.CreateDIBSection(
            screenDc,
            ref info,
            NativeMethods.DibRgbColors,
            out _surfaceBits,
            IntPtr.Zero,
            0);
        if (_surfaceBitmap == IntPtr.Zero || _surfaceBits == IntPtr.Zero)
        {
            DestroyLayeredSurface();
            return;
        }
        _surfaceOldBitmap = NativeMethods.SelectObject(_surfaceDc, _surfaceBitmap);
        _surfaceSize = size;
    }

    private void DestroyLayeredSurface()
    {
        _layeredSurfaceNeedsFullCopy = true;
        _lastPresentedBitmap = null;
        _lastPresentedWindow = IntPtr.Zero;
        if (_surfaceDc != IntPtr.Zero && _surfaceOldBitmap != IntPtr.Zero)
            NativeMethods.SelectObject(_surfaceDc, _surfaceOldBitmap);
        if (_surfaceBitmap != IntPtr.Zero) NativeMethods.DeleteObject(_surfaceBitmap);
        if (_surfaceDc != IntPtr.Zero) NativeMethods.DeleteDC(_surfaceDc);
        _surfaceDc = IntPtr.Zero;
        _surfaceBitmap = IntPtr.Zero;
        _surfaceOldBitmap = IntPtr.Zero;
        _surfaceBits = IntPtr.Zero;
        _surfaceSize = Size.Empty;
    }
}
