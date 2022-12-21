namespace WaveformAudioFile.Tests;

public class WaveformAudioFileTests
{
    FileInfo fread = new("./music.wav");
    FileInfo fwrite = new FileInfo("./out.wav");

    public WaveformAudioFileTests()
    {
        var dirName = "./test_outputs/";
        if (! Directory.Exists(dirName))
        {
            Directory.CreateDirectory(dirName);
        }
    }
    
    [Fact]
    public void ReadWaveformAudioFile()
    {
        // Open the wav file specified as the first argument
        var wavFile = WavFile.OpenWavFile(fread);

        // Get the number of audio channels in the wav file
        var numChannels = wavFile.NumChannels;

        // Create a buffer of 100 frames
        var buffer = new int[100 * numChannels];

        int framesRead;
        var resMin = -15559;
        var resMax = 15063;

        var min = int.MaxValue;
        var max = int.MinValue;

        do
        {
            // Read frames into buffer
            framesRead = wavFile.ReadFrames(buffer, 100);

            // Loop through frames and look for minimum and maximum value
            for (var s=0 ; s<framesRead * numChannels ; s++)
            {
                if (buffer[s] > max) max = buffer[s];
                if (buffer[s] < min) min = buffer[s];
            }
        }
        while (framesRead != 0);

        // Close the wavFile
        wavFile.Close();

        Assert.Equal(min, resMin);
        Assert.Equal(max, resMax);
    }
    
    [Fact]
    public void WriteWaveformAudioFile()
    {
        var sampleRate = 44100;		// Samples per second
        var duration = 5.0;		// Seconds

        // Calculate the number of frames required for specified duration
        var numFrames = (long)(duration * sampleRate);

        // Create a wav file with the name specified as the first argument
        var wavFile = WavFile.NewWavFile(this.fwrite, 2, numFrames, 16, sampleRate);

        // Create a buffer of 100 frames
        var buffer = new double[2][];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = new double[100];
        }
        
        // Initialise a local frame counter
        long frameCounter = 0;

        // Loop until all frames written
        while (frameCounter < numFrames)
        {
            // Determine how many frames to write, up to a maximum of the buffer size
            var remaining = wavFile.FramesRemaining;
            var toWrite = (remaining > 100) ? 100 : (int) remaining;

            // Fill the buffer, one tone per channel
            for (var s=0 ; s<toWrite ; s++, frameCounter++)
            {
                buffer[0][s] = Math.Sin(2.0 * Math.PI * 400 * frameCounter / sampleRate);
                buffer[1][s] = Math.Sin(2.0 * Math.PI * 500 * frameCounter / sampleRate);
            }

            // Write the buffer
            wavFile.WriteFrames(buffer, toWrite);
        }

        // Close the wavFile
        wavFile.Close();

        var fileExists = new FileInfo("./out.wav").Exists;
        Assert.True(fileExists);
    }
}