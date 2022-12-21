using System.IO;
using System.Linq;

namespace WaveformAudioFile
{
    public static class WaveFormAudioFileExtensions
    {
        public static void Write(this FileStream stream, sbyte[] array, int offset, int count)
        {
            var clrCast = (byte[]) (object) array;
            stream.Write(clrCast, offset, count);
        }
        
        public static  int Read(this FileStream stream, sbyte[] array, int offset, int count)
        {
            var clrCast = (byte[]) (object) array;
            return stream.Read(clrCast, offset, count);
        }
    }
}