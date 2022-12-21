using System.IO;
// ReSharper disable MemberCanBePrivate.Global

namespace WaveformAudioFile
{
	/// <summary>
	/// Wav file IO class
	/// Ported from Java code by A.Greensted
	/// http://www.labbookpages.co.uk
	///
	/// File format is based on the information from
	/// http://www.sonicspot.com/guide/wavefiles.html
	/// http://www.blitter.com/~russtopia/MIDI/~jglatt/tech/wave.htm
	/// </summary>
	public sealed class WavFile
	{
		/// <summary>
		/// States that our IO operations can be in.
		/// </summary>
		private enum IoState
		{
			Reading,
			Writing,
			Closed
		}
		
		private const int BufferSize = 4096;

		
		// WAV-specific constants.
		private const int FmtChunkId = 0x20746D66;
		private const int DataChunkId = 0x61746164;
		private const int RiffChunkId = 0x46464952;
		private const int RiffTypeId = 0x45564157;

#pragma warning disable CS0414
		private FileInfo _file; // File that will be read from or written to
#pragma warning restore CS0414
		
		private IoState _ioState; // Specifies the IO State of the Wav File (used for sanity checking)
		private int _bytesPerSample; // Number of bytes required to store a single sample
		private long _numFrames; // Number of frames within the data section
		private FileStream _oStream; // Output stream used for writing data
		private FileStream _iStream; // Input stream used for reading data
		private double _floatScale; // Scaling factor used for int <-> float conversion
		private double _floatOffset; // Offset factor used for int <-> float conversion
		private bool _wordAlignAdjust; // Specify if an extra byte at the end of the data chunk is required for word alignment

		// Wav Header.
		private int _numChannels; // 2 bytes unsigned, 0x0001 (1) to 0xFFFF (65,535)
		private long _sampleRate; // 4 bytes unsigned, 0x00000001 (1) to 0xFFFFFFFF (4,294,967,295)

		private int _blockAlign; // 2 bytes unsigned, 0x0001 (1) to 0xFFFF (65,535)
		private int _validBits; // 2 bytes unsigned, 0x0002 (2) to 0xFFFF (65,535)

		// Buffering
		private readonly sbyte[] _buffer; // Local buffer used for IO
		private int _bufferPointer; // Points to the current position in local buffer
		private int _bytesRead; // Bytes read after last read into local buffer
		private long _frameCounter; // Current number of frames read or written

		/// <summary>
		/// Cannot instantiate WavFile directly, must either use NewWavFile() or OpenWavFile().
		/// </summary>
		private WavFile()
		{
			_buffer = new sbyte[BufferSize];
		}

		/// <summary>
		/// Gets the number of channels.
		/// </summary>
		/// <returns> the number of channels </returns>
		public int NumChannels => _numChannels;

		/// <summary>
		/// Get the number of frames.
		/// </summary>
		/// <returns> the number of frames </returns>
		public long NumFrames => _numFrames;

		/// <summary>
		/// Get the number of frames remaining.
		/// </summary>
		/// <returns> the number of frames remaining </returns>
		public long FramesRemaining => _numFrames - _frameCounter;

		/// <summary>
		/// Get the sample rate.
		/// </summary>
		/// <returns> the sample rate </returns>
		public long SampleRate => _sampleRate;

		/// <summary>
		/// Get the number of valid bits.
		/// </summary>
		/// <returns> the number of valid bits </returns>
		public int ValidBits => _validBits;

		/// <summary>
		/// Initialize a new WavFile object for writing into the specified file.
		/// </summary>
		/// <param name="file"> the file to write to </param>
		/// <param name="numChannels"> the number of channels to use </param>
		/// <param name="numFrames"> the number of frames to use </param>
		/// <param name="validBits"> the number of valid bits to use </param>
		/// <param name="sampleRate"> the sample rate for the new file </param>
		/// <returns> the WavFile object </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public static WavFile NewWavFile(FileInfo file, int numChannels, long numFrames, int validBits, long sampleRate)
		{
			// Instantiate new WavFile and initialise
			var wavFile = new WavFile
			{
				_file = file,
				_numChannels = numChannels,
				_numFrames = numFrames,
				_sampleRate = sampleRate,
				_bytesPerSample = (validBits + 7) / 8
			};
			wavFile._blockAlign = wavFile._bytesPerSample * numChannels;
			wavFile._validBits = validBits;

			// Sanity check arguments
			if (numChannels < 1 || numChannels > 65535)
			{
				throw new WaveformAudioFileException("Illegal number of channels, valid range 1 to 65536");
			}
			if (numFrames < 0)
			{
				throw new WaveformAudioFileException("Number of frames must be positive");
			}
			if (validBits < 2 || validBits > 65535)
			{
				throw new WaveformAudioFileException("Illegal number of valid bits, valid range 2 to 65536");
			}
			if (sampleRate < 0)
			{
				throw new WaveformAudioFileException("Sample rate must be positive");
			}

			// Create output stream for writing data
			wavFile._oStream = new FileStream(file.FullName, FileMode.Create, FileAccess.Write);
			
			// Calculate the chunk sizes
			var dataChunkSize = wavFile._blockAlign * numFrames;
			var mainChunkSize = 4 + 8 + 16 + 8 + dataChunkSize;

			// Chunks must be word aligned, so if odd number of audio data bytes
			// adjust the main chunk size
			if (dataChunkSize % 2 == 1)
			{
				mainChunkSize += 1;
				wavFile._wordAlignAdjust = true;
			}
			else
			{
				wavFile._wordAlignAdjust = false;
			}

			// Set the main chunk size
			PutLe(RiffChunkId, wavFile._buffer, 0, 4);
			PutLe(mainChunkSize, wavFile._buffer, 4, 4);
			PutLe(RiffTypeId, wavFile._buffer, 8, 4);

			// Write out the header
			wavFile._oStream.Write(wavFile._buffer, 0, 12);

			// Put format data in buffer
			var averageBytesPerSecond = sampleRate * wavFile._blockAlign;

			PutLe(FmtChunkId, wavFile._buffer, 0, 4); // Chunk ID
			PutLe(16, wavFile._buffer, 4, 4); // Chunk Data Size
			PutLe(1, wavFile._buffer, 8, 2); // Compression Code (Uncompressed)
			PutLe(numChannels, wavFile._buffer, 10, 2); // Number of channels
			PutLe(sampleRate, wavFile._buffer, 12, 4); // Sample Rate
			PutLe(averageBytesPerSecond, wavFile._buffer, 16, 4); // Average Bytes Per Second
			PutLe(wavFile._blockAlign, wavFile._buffer, 20, 2); // Block Align
			PutLe(validBits, wavFile._buffer, 22, 2); // Valid Bits

			// Write Format Chunk
			wavFile._oStream.Write(wavFile._buffer, 0, 24);

			// Start Data Chunk
			PutLe(DataChunkId, wavFile._buffer, 0, 4); // Chunk ID
			PutLe(dataChunkSize, wavFile._buffer, 4, 4); // Chunk Data Size

			// Write Format Chunk
			wavFile._oStream.Write(wavFile._buffer, 0, 8);

			// Calculate the scaling factor for converting to a normalised double
			if (wavFile._validBits > 8)
			{
				// If more than 8 validBits, data is signed
				// Conversion required multiplying by magnitude of max positive value
				wavFile._floatOffset = 0;
				wavFile._floatScale = long.MaxValue >> (64 - wavFile._validBits);
			}
			else
			{
				// Else if 8 or less validBits, data is unsigned
				// Conversion required dividing by max positive value
				wavFile._floatOffset = 1;
				wavFile._floatScale = 0.5 * ((1 << wavFile._validBits) - 1);
			}

			// Finally, set the IO State
			wavFile._bufferPointer = 0;
			wavFile._bytesRead = 0;
			wavFile._frameCounter = 0;
			wavFile._ioState = IoState.Writing;

			return wavFile;
		}

		/// <summary>
		/// Read WAV contents from a provided file.
		/// </summary>
		/// <param name="file"> the file </param>
		/// <returns> the wav file </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public static WavFile OpenWavFile(FileInfo file)
		{
			// Instantiate new WavFile and store the file reference
			var wavFile = new WavFile
			{
				_file = file,
				// Create a new file input stream for reading file data
				_iStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read)
			};

			// Read the first 12 bytes of the file
			var bytesRead = wavFile._iStream.Read(wavFile._buffer, 0, 12);
			if (bytesRead != 12)
			{
				throw new WaveformAudioFileException("Not enough wav file bytes for header");
			}

			// Extract parts from the header
			var riffChunkId = GetLe(wavFile._buffer, 0, 4);
			var chunkSize = GetLe(wavFile._buffer, 4, 4);
			var riffTypeId = GetLe(wavFile._buffer, 8, 4);

			// Check the header bytes contains the correct signature
			if (riffChunkId != RiffChunkId)
			{
				throw new WaveformAudioFileException("Invalid Wav Header data, incorrect riff chunk ID");
			}
			if (riffTypeId != RiffTypeId)
			{
				throw new WaveformAudioFileException("Invalid Wav Header data, incorrect riff type ID");
			}

			// Check that the file size matches the number of bytes listed in header
			if (file.Length != chunkSize+8)
			{
				throw new WaveformAudioFileException("Header chunk size (" + chunkSize + ") does not match file size (" + file.Length + ")");
			}

			var foundFormat = false;
			// ReSharper disable once RedundantAssignment
			var foundData = false;

			// Search for the Format and Data Chunks
			while (true)
			{
				// Read the first 8 bytes of the chunk (ID and chunk size)
				bytesRead = wavFile._iStream.Read(wavFile._buffer, 0, 8);
				if (bytesRead == -1)
				{
					throw new WaveformAudioFileException("Reached end of file without finding format chunk");
				}
				if (bytesRead != 8)
				{
					throw new WaveformAudioFileException("Could not read chunk header");
				}

				// Extract the chunk ID and Size
				var chunkId = GetLe(wavFile._buffer, 0, 4);
				chunkSize = GetLe(wavFile._buffer, 4, 4);

				// Word align the chunk size
				// chunkSize specifies the number of bytes holding data. However,
				// the data should be word aligned (2 bytes) so we need to calculate
				// the actual number of bytes in the chunk
				var numChunkBytes = (chunkSize % 2 == 1) ? chunkSize+1 : chunkSize;

				if (chunkId == FmtChunkId)
				{
					// Flag that the format chunk has been found
					foundFormat = true;

					// Read in the header info
					// ReSharper disable once RedundantAssignment
					bytesRead = wavFile._iStream.Read(wavFile._buffer, 0, 16);

					// Check this is uncompressed data
					var compressionCode = (int) GetLe(wavFile._buffer, 0, 2);
					if (compressionCode != 1)
					{
						throw new WaveformAudioFileException("Compression Code " + compressionCode + " not supported");
					}

					// Extract the format information
					wavFile._numChannels = (int) GetLe(wavFile._buffer, 2, 2);
					wavFile._sampleRate = GetLe(wavFile._buffer, 4, 4);
					wavFile._blockAlign = (int) GetLe(wavFile._buffer, 12, 2);
					wavFile._validBits = (int) GetLe(wavFile._buffer, 14, 2);

					if (wavFile._numChannels == 0)
					{
						throw new WaveformAudioFileException("Number of channels specified in header is equal to zero");
					}
					if (wavFile._blockAlign == 0)
					{
						throw new WaveformAudioFileException("Block Align specified in header is equal to zero");
					}
					if (wavFile._validBits < 2)
					{
						throw new WaveformAudioFileException("Valid Bits specified in header is less than 2");
					}
					if (wavFile._validBits > 64)
					{
						throw new WaveformAudioFileException("Valid Bits specified in header is greater than 64, this is greater than a long can hold");
					}

					// Calculate the number of bytes required to hold 1 sample
					wavFile._bytesPerSample = (wavFile._validBits + 7) / 8;
					if (wavFile._bytesPerSample * wavFile._numChannels != wavFile._blockAlign)
					{
						throw new WaveformAudioFileException("Block Align does not agree with bytes required for validBits and number of channels");
					}

					// Account for number of format bytes and then skip over
					// any extra format bytes
					numChunkBytes -= 16;
					if (numChunkBytes > 0)
					{
						// wavFile.iStream.skip(numChunkBytes);
						wavFile._iStream.Seek(numChunkBytes, SeekOrigin.Current);
					}
				}
				else if (chunkId == DataChunkId)
				{
					// Check if we've found the format chunk,
					// If not, throw an exception as we need the format information
					// before we can read the data chunk
					if (foundFormat == false)
					{
						throw new WaveformAudioFileException("Data chunk found before Format chunk");
					}

					// Check that the chunkSize (wav data length) is a multiple of the
					// block align (bytes per frame)
					if (chunkSize % wavFile._blockAlign != 0)
					{
						throw new WaveformAudioFileException("Data Chunk size is not multiple of Block Align");
					}

					// Calculate the number of frames
					wavFile._numFrames = chunkSize / wavFile._blockAlign;

					// Flag that we've found the wave data chunk
					foundData = true;

					break;
				}
				else
				{
					// If an unknown chunk ID is found, just skip over the chunk data
					// wavFile.iStream.skip(numChunkBytes);
					wavFile._iStream.Seek(numChunkBytes, SeekOrigin.Current);
				}
			}

			// Throw an exception if no data chunk has been found
			if (foundData == false)
			{
				throw new WaveformAudioFileException("Did not find a data chunk");
			}

			// Calculate the scaling factor for converting to a normalised double
			if (wavFile._validBits > 8)
			{
				// If more than 8 validBits, data is signed
				// Conversion required dividing by magnitude of max negative value
				wavFile._floatOffset = 0;
				wavFile._floatScale = 1 << (wavFile._validBits - 1);
			}
			else
			{
				// Else if 8 or less validBits, data is unsigned
				// Conversion required dividing by max positive value
				wavFile._floatOffset = -1;
				wavFile._floatScale = 0.5 * ((1 << wavFile._validBits) - 1);
			}

			wavFile._bufferPointer = 0;
			wavFile._bytesRead = 0;
			wavFile._frameCounter = 0;
			wavFile._ioState = IoState.Reading;

			return wavFile;
		}

		/// <summary>
		/// Get little-endian data from the buffer.
		/// </summary>
		/// <param name="buffer"> the buffer to read from </param>
		/// <param name="pos"> the starting position </param>
		/// <param name="numBytes"> the number of bytes to read </param>
		/// <returns> a little-endian long </returns>
		private static long GetLe(sbyte[] buffer, int pos, int numBytes)
		{
			numBytes--;
			pos += numBytes;

			long val = buffer[pos] & 0xFF;
			for (var b = 0 ; b < numBytes ; b++)
			{
				val = (val << 8) + (buffer[--pos] & 0xFF);
			}

			return val;
		}

		/// <summary>
		/// Put little-endian data into the buffer.
		/// </summary>
		/// <param name="val"> the data to write into the buffer </param>
		/// <param name="buffer"> the buffer to write to </param>
		/// <param name="pos"> the position to write to </param>
		/// <param name="numBytes"> the number of bytes to write </param>
		private static void PutLe(long val, sbyte[] buffer, int pos, int numBytes)
		{
			for (var b = 0 ; b < numBytes ; b++)
			{
				buffer[pos] = unchecked((sbyte)(val & 0xFF));
				val >>= 8;
				pos++;
			}
		}

		/// <summary>
		/// Write a sample to our buffer.
		/// </summary>
		/// <param name="val"> the sample to write </param>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		private void WriteSample(long val)
		{
			for (var b = 0 ; b < _bytesPerSample ; b++)
			{
				if (_bufferPointer == BufferSize)
				{
					_oStream.Write(_buffer, 0, BufferSize);
					_bufferPointer = 0;
				}

				_buffer[_bufferPointer] = unchecked((sbyte)(val & 0xFF));
				val >>= 8;
				_bufferPointer++;
			}
		}

		/// <summary>
		/// Read a sample from the buffer.
		/// </summary>
		/// <returns> the sample read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		private long ReadSample()
		{
			long val = 0;

			for (var b = 0 ; b < _bytesPerSample ; b++)
			{
				if (_bufferPointer == _bytesRead)
				{
					var read = _iStream.Read(_buffer, 0, BufferSize);
					if (read == -1)
					{
						throw new WaveformAudioFileException("Not enough data available");
					}
					_bytesRead = read;
					_bufferPointer = 0;
				}

				int v = _buffer[_bufferPointer];
				if (b < _bytesPerSample-1 || _bytesPerSample == 1)
				{
					v &= 0xFF;
				}
				val += v << (b * 8);

				_bufferPointer++;
			}

			return val;
		}

		/// <summary>
		/// Read some number of frames from the buffer into a flat int array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(int[] sampleBuffer, int numFramesToRead)
		{
			return ReadFrames(sampleBuffer, 0, numFramesToRead);
		}

		/// <summary>
		/// Read some number of frames from a specific offset in the buffer into a flat int array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="offset"> the buffer offset to read from </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(int[] sampleBuffer, int offset, int numFramesToRead)
		{
			if (_ioState != IoState.Reading)
			{
				throw new IOException("Cannot read from WavFile instance");
			}

			for (var f = 0 ; f < numFramesToRead ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					sampleBuffer[offset] = (int) ReadSample();
					offset++;
				}

				_frameCounter++;
			}

			return numFramesToRead;
		}

		/// <summary>
		/// Read some number of frames from the buffer into a multi-dimensional int array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(int[][] sampleBuffer, int numFramesToRead)
		{
			return ReadFrames(sampleBuffer, 0, numFramesToRead);
		}

		/// <summary>
		/// Read some number of frames from a specific offset in the buffer into a multi-dimensional int
		/// array.
		/// s </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="offset"> the buffer offset to read from </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(int[][] sampleBuffer, int offset, int numFramesToRead)
		{
			if (_ioState != IoState.Reading)
			{
				throw new IOException("Cannot read from WavFile instance");
			}

			for (var f = 0 ; f < numFramesToRead ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					sampleBuffer[c][offset] = (int) ReadSample();
				}

				offset++;
				_frameCounter++;
			}

			return numFramesToRead;
		}

		/// <summary>
		/// Write some number of frames into the buffer from a flat int array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(int[] sampleBuffer, int numFramesToWrite)
		{
			return WriteFrames(sampleBuffer, 0, numFramesToWrite);
		}

		/// <summary>
		/// Write some number of frames into the buffer at a specific offset from a flat int array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="offset"> the buffer offset to write into </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(int[] sampleBuffer, int offset, int numFramesToWrite)
		{
			if (_ioState != IoState.Writing)
			{
				throw new IOException("Cannot write to WavFile instance");
			}

			for (var f = 0 ; f < numFramesToWrite ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					WriteSample(sampleBuffer[offset]);
					offset++;
				}

				_frameCounter++;
			}

			return numFramesToWrite;
		}

		/// <summary>
		/// Write some number of frames into the buffer from a multi-dimensional int array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(int[][] sampleBuffer, int numFramesToWrite)
		{
			return WriteFrames(sampleBuffer, 0, numFramesToWrite);
		}

		/// <summary>
		/// Write some number of frames into the buffer at a specific offset from a multi-dimensional int
		/// array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="offset"> the buffer offset to write into </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(int[][] sampleBuffer, int offset, int numFramesToWrite)
		{
			if (_ioState != IoState.Writing)
			{
				throw new IOException("Cannot write to WavFile instance");
			}

			for (var f = 0 ; f < numFramesToWrite ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					WriteSample(sampleBuffer[c][offset]);
				}

				offset++;
				_frameCounter++;
			}

			return numFramesToWrite;
		}

		/// <summary>
		/// Read some number of frames from the buffer into a flat long array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(long[] sampleBuffer, int numFramesToRead)
		{
			return ReadFrames(sampleBuffer, 0, numFramesToRead);
		}

		/// <summary>
		/// Read some number of frames from a specific offset in the buffer into a flat long array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="offset"> the buffer offset to read from </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(long[] sampleBuffer, int offset, int numFramesToRead)
		{
			if (_ioState != IoState.Reading)
			{
				throw new IOException("Cannot read from WavFile instance");
			}

			for (var f = 0 ; f < numFramesToRead ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					sampleBuffer[offset] = ReadSample();
					offset++;
				}

				_frameCounter++;
			}

			return numFramesToRead;
		}

		/// <summary>
		/// Read some number of frames from the buffer into a multi-dimensional long array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(long[][] sampleBuffer, int numFramesToRead)
		{
			return ReadFrames(sampleBuffer, 0, numFramesToRead);
		}

		/// <summary>
		/// Read some number of frames from a specific offset in the buffer into a multi-dimensional long
		/// array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="offset"> the buffer offset to read from </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(long[][] sampleBuffer, int offset, int numFramesToRead)
		{
			if (_ioState != IoState.Reading)
			{
				throw new IOException("Cannot read from WavFile instance");
			}

			for (var f = 0 ; f < numFramesToRead ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					sampleBuffer[c][offset] = ReadSample();
				}

				offset++;
				_frameCounter++;
			}

			return numFramesToRead;
		}

		/// <summary>
		/// Write some number of frames into the buffer from a flat long array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(long[] sampleBuffer, int numFramesToWrite)
		{
			return WriteFrames(sampleBuffer, 0, numFramesToWrite);
		}

		/// <summary>
		/// Write some number of frames into the buffer at a specific offset from a flat long array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="offset"> the buffer offset to write into </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(long[] sampleBuffer, int offset, int numFramesToWrite)
		{
			if (_ioState != IoState.Writing)
			{
				throw new IOException("Cannot write to WavFile instance");
			}

			for (var f = 0 ; f < numFramesToWrite ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					WriteSample(sampleBuffer[offset]);
					offset++;
				}

				_frameCounter++;
			}

			return numFramesToWrite;
		}

		/// <summary>
		/// Write some number of frames into the buffer from a multi-dimensional long array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(long[][] sampleBuffer, int numFramesToWrite)
		{
			return WriteFrames(sampleBuffer, 0, numFramesToWrite);
		}

		/// <summary>
		/// Write some number of frames into the buffer at a specific offset from a multi-dimensional
		/// long array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="offset"> the buffer offset to write into </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(long[][] sampleBuffer, int offset, int numFramesToWrite)
		{
			if (_ioState != IoState.Writing)
			{
				throw new IOException("Cannot write to WavFile instance");
			}

			for (var f = 0 ; f < numFramesToWrite ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					WriteSample(sampleBuffer[c][offset]);
				}

				offset++;
				_frameCounter++;
			}

			return numFramesToWrite;
		}

		/// <summary>
		/// Read some number of frames from the buffer into a flat double array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(double[] sampleBuffer, int numFramesToRead)
		{
			return ReadFrames(sampleBuffer, 0, numFramesToRead);
		}

		/// <summary>
		/// Read some number of frames from a specific offset in the buffer into a flat double array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="offset"> the buffer offset to read from </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(double[] sampleBuffer, int offset, int numFramesToRead)
		{
			if (_ioState != IoState.Reading)
			{
				throw new IOException("Cannot read from WavFile instance");
			}

			for (var f = 0 ; f < numFramesToRead ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					sampleBuffer[offset] = _floatOffset + ReadSample() / _floatScale;
					offset++;
				}

				_frameCounter++;
			}

			return numFramesToRead;
		}

		/// <summary>
		/// Read some number of frames from the buffer into a multi-dimensional double array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(double[][] sampleBuffer, int numFramesToRead)
		{
			return ReadFrames(sampleBuffer, 0, numFramesToRead);
		}

		/// <summary>
		/// Read some number of frames from a specific offset in the buffer into a multi-dimensional
		/// double array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples into </param>
		/// <param name="offset"> the buffer offset to read from </param>
		/// <param name="numFramesToRead"> the number of frames to read </param>
		/// <returns> the number of frames read </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int ReadFrames(double[][] sampleBuffer, int offset, int numFramesToRead)
		{
			if (_ioState != IoState.Reading)
			{
				throw new IOException("Cannot read from WavFile instance");
			}

			for (var f = 0 ; f < numFramesToRead ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					sampleBuffer[c][offset] = _floatOffset + ReadSample() / _floatScale;
				}

				offset++;
				_frameCounter++;
			}

			return numFramesToRead;
		}

		/// <summary>
		/// Write some number of frames into the buffer from a flat double array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(double[] sampleBuffer, int numFramesToWrite)
		{
			return WriteFrames(sampleBuffer, 0, numFramesToWrite);
		}

		/// <summary>
		/// Write some number of frames into the buffer at a specific offset from a flat double array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="offset"> the buffer offset to write into </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(double[] sampleBuffer, int offset, int numFramesToWrite)
		{
			if (_ioState != IoState.Writing)
			{
				throw new IOException("Cannot write to WavFile instance");
			}

			for (var f = 0 ; f < numFramesToWrite ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					WriteSample((long)(_floatScale * (_floatOffset + sampleBuffer[offset])));
					offset++;
				}

				_frameCounter++;
			}

			return numFramesToWrite;
		}

		/// <summary>
		/// Write some number of frames into the buffer from a multi-dimensional double array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(double[][] sampleBuffer, int numFramesToWrite)
		{
			return WriteFrames(sampleBuffer, 0, numFramesToWrite);
		}

		/// <summary>
		/// Write some number of frames into the buffer at a specific offset from a multi-dimensional
		/// double array.
		/// </summary>
		/// <param name="sampleBuffer"> the buffer to read samples from </param>
		/// <param name="offset"> the buffer offset to write into </param>
		/// <param name="numFramesToWrite"> the number of frames to write </param>
		/// <returns> the number of frames written </returns>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		/// <exception cref="WaveformAudioFileException"> a WavFile-specific exception </exception>
		public int WriteFrames(double[][] sampleBuffer, int offset, int numFramesToWrite)
		{
			if (_ioState != IoState.Writing)
			{
				throw new IOException("Cannot write to WavFile instance");
			}

			for (var f = 0 ; f < numFramesToWrite ; f++)
			{
				if (_frameCounter == _numFrames)
				{
					return f;
				}

				for (var c = 0 ; c < _numChannels ; c++)
				{
					WriteSample((long)(_floatScale * (_floatOffset + sampleBuffer[c][offset])));
				}

				offset++;
				_frameCounter++;
			}

			return numFramesToWrite;
		}

		/// <summary>
		/// Close the WavFile.
		/// </summary>
		/// <exception cref="IOException"> Signals that an I/O exception has occurred </exception>
		public void Close()
		{
			// Close the input stream and set to null
			if (_iStream != null)
			{
				_iStream.Close();
				_iStream = null;
			}

			if (_oStream != null)
			{
				// Write out anything still in the local buffer
				if (_bufferPointer > 0)
				{
					_oStream.Write(_buffer, 0, _bufferPointer);
				}

				// If an extra byte is required for word alignment, add it to the end
				if (_wordAlignAdjust)
				{
					_oStream.WriteByte(0);
				}

				// Close the stream and set to null
				_oStream.Close();
				_oStream = null;
			}

			// Flag that the stream is closed
			_ioState = IoState.Closed;
		}
	}
}