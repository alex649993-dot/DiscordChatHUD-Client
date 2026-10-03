using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
namespace DiscordChatHUD.Services;
internal sealed partial class MediaCache
{
    private static readonly AsyncLocal<LiveFrameExport?> LiveExport = new();
    // One encoder on the compositing thread plus at most three ahead decoders.
    private static int ExportDecoderThreads => Math.Clamp(RelayDecodeParallelism - (LiveExport.Value is null ? 0 : 1), 1, 4);
    internal sealed class LiveFrameExport(Stream output, bool highQuality)
    {
        private BinaryWriter? _writer;
        private byte[]? _pixels;
        private int _count, _written;
        private Size _size;
        internal bool Failed { get; private set; }
        internal bool Complete => !Failed && _count>=2 && _written==_count;
        private static readonly SixLabors.ImageSharp.Configuration Config = Configuration();
        private static SixLabors.ImageSharp.Configuration Configuration(){var c=SixLabors.ImageSharp.Configuration.Default.Clone();c.MaxDegreeOfParallelism=1;return c;}
        private static readonly WebpEncoder Encoder = new(){FileFormat=WebpFileFormatType.Lossless,Method=WebpEncodingMethod.Level0,Quality=0,TransparentColorMode=WebpTransparentColorMode.Preserve};
        internal void Begin(Size size,IReadOnlyList<int> indexes,IReadOnlyList<int> sourceDelays)
        {
            if(Failed)return;
            if(_writer is not null){Failed=true;return;} // A fallback decoder must never rewrite an already visible prefix.
            try
            {
                _size=size;_count=indexes.Count;_pixels=new byte[checked(size.Width*size.Height*4)];
                _writer=new BinaryWriter(output,System.Text.Encoding.UTF8,true);
                _writer.Write(highQuality ? "DHSFRW02"u8 : "DHSFRW01"u8);_writer.Write(size.Width);_writer.Write(size.Height);_writer.Write(_count);_writer.Write(0);
                for(int i=0;i<_count;i++){long delay=0;int end=i+1<_count?indexes[i+1]:sourceDelays.Count;for(int j=indexes[i];j<end;j++)delay+=sourceDelays[j];_writer.Write((int)Math.Clamp(delay,10,int.MaxValue));}
            }
            catch{Failed=true;}
        }
        internal void Add(int index,Bitmap frame)
        {
            if(Failed || _writer is null)return;
            try
            {
                if(index!=_written || frame.Size!=_size)throw new InvalidDataException("Live frame order");
                var bits=frame.LockBits(new Rectangle(Point.Empty,_size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
                try{for(int y=0;y<_size.Height;y++)Marshal.Copy(bits.Scan0+y*bits.Stride,_pixels!,y*_size.Width*4,_size.Width*4);}
                finally{frame.UnlockBits(bits);}
                using var image=SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(Config,_pixels!,_size.Width,_size.Height);
                using var memory=new MemoryStream();image.Save(memory,Encoder);
                if(output.Position+4+memory.Length>CompressedBitmapFrames.MaxPackBytes)throw new InvalidDataException("Live pack size");
                _writer.Write(checked((int)memory.Length));memory.Position=0;memory.CopyTo(output);_written++;
                // Make whole records visible immediately to the HTTP reader.
                output.Flush();
                if(_written==_count)_pixels=null;
            }
            catch{Failed=true;_pixels=null;}
        }
    }
}
