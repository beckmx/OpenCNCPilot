using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using AForge.Video;
using FFMpegCore;
using FFMpegCore.Pipes;

namespace OpenCNCPilot
{
    public partial class MainWindow : Window
    {
        private List<Bitmap> capturedFrames = new List<Bitmap>();
        private Timer captureTimer;
        private bool isRecording = false;
        private string outputVideoPath = "timelapse.mp4";

        private void StartRecording_Click(object sender, RoutedEventArgs e)
        {
            if (!isRecording)
            {
                isRecording = true;
                capturedFrames.Clear();
                captureTimer = new Timer(CaptureFrame, null, 0, 5000);
                MessageBox.Show("Timelapse recording started.");
            }
        }

        private void StopRecording_Click(object sender, RoutedEventArgs e)
        {
            if (isRecording)
            {
                isRecording = false;
                captureTimer?.Dispose();
                Task.Run(() => CreateTimelapse());
                MessageBox.Show("Timelapse recording stopped. Generating video...");
            }
        }

        private void CaptureFrame(object state)
        {
            if (VideoStream.Source is BitmapImage bitmapImage)
            {
                Bitmap bitmap = BitmapImageToBitmap(bitmapImage);
                capturedFrames.Add(bitmap);
            }
        }

        private async Task CreateTimelapse()
        {
            if (capturedFrames.Count == 0)
                return;

            var frameSources = new List<IBitmapFrame>();
            foreach (var frame in capturedFrames)
            {
                frameSources.Add(new BitmapFrameWrapper(frame));
            }

            await FFMpegArguments
                .FromPipeInput(new RawVideoPipeSource(frameSources) { FrameRate = 2 })
                .OutputToFile(outputVideoPath, overwrite: true, options => options.WithVideoCodec("libx264"))
                .ProcessAsync();

            MessageBox.Show("Timelapse video saved: " + outputVideoPath);
        }

        private Bitmap BitmapImageToBitmap(BitmapImage bitmapImage)
        {
            using (MemoryStream outStream = new MemoryStream())
            {
                BitmapEncoder encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
                encoder.Save(outStream);
                return new Bitmap(outStream);
            }
        }
    }
}
