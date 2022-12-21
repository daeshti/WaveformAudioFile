using System;

namespace WaveformAudioFile
{
	/// <summary>
	/// WaveFile-specific exception class.
	/// </summary>
	public class WaveformAudioFileException : Exception
	{
		/// <summary>
		/// Create a new WaveformAudioFile-specific exception with a given message.
		/// </summary>
		/// <param name="message"> the message </param>
		public WaveformAudioFileException(string message) : base(message)
		{
		}
	}
}