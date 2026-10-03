using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SixLabors.ImageSharp.Memory;

namespace DiscordChatHUD.Services;

// Large decoder frames must be returned to Windows when disposed, rather than
// waiting in the managed large-object heap for a later generation-2 collection.
internal sealed class TransientImageAllocator : MemoryAllocator
{
    private readonly MemoryAllocator _small = MemoryAllocator.Create(new MemoryAllocatorOptions
    { MaximumPoolSizeMegabytes = 1, AllocationLimitMegabytes = 192 });
    protected override int GetBufferCapacityInBytes() => 4 * 1024 * 1024;
    public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.None)
    {
        var bytes = checked((long)length * Unsafe.SizeOf<T>());
        if (bytes <= 64 * 1024 || RuntimeHelpers.IsReferenceOrContainsReferences<T>()) return _small.Allocate<T>(length, options);
        if (bytes > 192L * 1024 * 1024) throw new InvalidOperationException("Image allocation limit exceeded");
        return new NativeImageMemory<T>(length, bytes);
    }

    private sealed unsafe class NativeImageMemory<T> : MemoryManager<T> where T : struct
    {
        private readonly VirtualBuffer _buffer;
        private readonly int _length;
        public NativeImageMemory(int length, long bytes) { _length = length; _buffer = new VirtualBuffer((nuint)bytes); }
        public override Span<T> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_buffer.IsClosed, this);
            return new Span<T>((void*)_buffer.DangerousGetHandle(), _length);
        }
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)_length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            ObjectDisposedException.ThrowIf(_buffer.IsClosed, this);
            return new MemoryHandle((byte*)_buffer.DangerousGetHandle() + elementIndex * Unsafe.SizeOf<T>(), default, this);
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => _buffer.Dispose();
    }

    private sealed class VirtualBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        public VirtualBuffer(nuint bytes) : base(true)
        {
            SetHandle(VirtualAlloc(IntPtr.Zero, bytes, 0x3000, 0x04));
            if (IsInvalid) throw new OutOfMemoryException("Image buffer allocation failed");
        }
        protected override bool ReleaseHandle() => VirtualFree(handle, 0, 0x8000);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, nuint size, uint type);
    }
}
