﻿using Microsoft.Win32;
using OpenCNCPilot.Communication;
using OpenCNCPilot.GCode;
using OpenCNCPilot.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using System.Drawing;
using AForge.Video;
using System.Configuration;

using System.Drawing.Imaging;
using System.Diagnostics;

namespace OpenCNCPilot
{
	public partial class MainWindow : Window, INotifyPropertyChanged
	{
        private MJPEGStream stream;

        Machine machine = new Machine();

		OpenFileDialog openFileDialogGCode = new OpenFileDialog() { Filter = Constants.FileFilterGCode };
		SaveFileDialog saveFileDialogGCode = new SaveFileDialog() { Filter = Constants.FileFilterGCode };
		OpenFileDialog openFileDialogHeightMap = new OpenFileDialog() { Filter = Constants.FileFilterHeightMap };
		SaveFileDialog saveFileDialogHeightMap = new SaveFileDialog() { Filter = Constants.FileFilterHeightMap };

		GCodeFile ToolPath { get; set; } = GCodeFile.Empty;
		HeightMap Map { get; set; }

		bool HeightMapApplied { get; set; } = false;

		GrblSettingsWindow settingsWindow = new GrblSettingsWindow();

		public event PropertyChangedEventHandler PropertyChanged;

        //timelapse variables
        private DispatcherTimer timelapseTimer;
        private List<string> imagePaths = new List<string>();
        private string outputFolder = Path.Combine(Path.GetTempPath(), "Timelapse");
        private bool isRecording = false;

        private void RaisePropertyChanged(string propertyName)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

        private void StartTimelapse_Click(object sender, RoutedEventArgs e)
        {
            if (isRecording) return;
            isRecording = true;
            Directory.CreateDirectory(outputFolder);
            imagePaths.Clear();

            timelapseTimer = new DispatcherTimer();
            timelapseTimer.Interval = TimeSpan.FromSeconds(5);
            timelapseTimer.Tick += CaptureFrame;
            timelapseTimer.Start();
        }

        private int frameCounter = 0; // Add this as a class-level variable

        private void CaptureFrame(object sender, EventArgs e)
        {
            if (VideoStream.Source is BitmapImage bitmapImage)
            {
                string filePath = Path.Combine(outputFolder, $"frame_{frameCounter:D4}.jpg"); // Sequential naming
                frameCounter++; // Increment the counter

                using (FileStream fileStream = new FileStream(filePath, FileMode.Create))
                {
                    BitmapEncoder encoder = new JpegBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
                    encoder.Save(fileStream);
                }

                imagePaths.Add(filePath);
            }
        }


        private void StopTimelapse_Click(object sender, RoutedEventArgs e)
        {
            if (!isRecording) return;
            isRecording = false;
            timelapseTimer?.Stop();
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg", "ffmpeg.exe");

            // Generate video using FFmpeg
            string videoPath = Path.Combine(outputFolder, "timelapse.mp4");
            string ffmpegCommand = $"-framerate 10 -i \"{outputFolder}\\frame_%04d.jpg\" -c:v libx264 -r 30 \"{videoPath}\"";
			Console.WriteLine(ffmpegPath);
			Console.WriteLine(ffmpegCommand);


            Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = ffmpegCommand,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            MessageBox.Show($"Timelapse saved to: {videoPath}");
        }

        public MainWindow()
		{
			AppDomain.CurrentDomain.UnhandledException += UnhandledException;
			InitializeComponent();


            UrlInput.Text = Properties.Settings.Default.StreamURL;
            Console.WriteLine("Saved URL: " + Properties.Settings.Default.StreamURL);
            openFileDialogGCode.FileOk += OpenFileDialogGCode_FileOk;
			saveFileDialogGCode.FileOk += SaveFileDialogGCode_FileOk;
			openFileDialogHeightMap.FileOk += OpenFileDialogHeightMap_FileOk;
			saveFileDialogHeightMap.FileOk += SaveFileDialogHeightMap_FileOk;

			machine.ConnectionStateChanged += Machine_ConnectionStateChanged;

			machine.NonFatalException += Machine_NonFatalException;
			machine.Info += Machine_Info;
			machine.LineReceived += Machine_LineReceived;
			machine.LineReceived += settingsWindow.LineReceived;
			machine.StatusReceived += Machine_StatusReceived;
			machine.LineSent += Machine_LineSent;

			machine.PositionUpdateReceived += Machine_PositionUpdateReceived;
			machine.StatusChanged += Machine_StatusChanged;
			machine.DistanceModeChanged += Machine_DistanceModeChanged;
			machine.UnitChanged += Machine_UnitChanged;
			machine.PlaneChanged += Machine_PlaneChanged;
			machine.BufferStateChanged += Machine_BufferStateChanged;
			machine.OperatingModeChanged += Machine_OperatingMode_Changed;
			machine.FileChanged += Machine_FileChanged;
			machine.FilePositionChanged += Machine_FilePositionChanged;
			machine.ProbeFinished += Machine_ProbeFinished;
			machine.OverrideChanged += Machine_OverrideChanged;
			machine.PinStateChanged += Machine_PinStateChanged;

			Machine_OperatingMode_Changed();
			Machine_PositionUpdateReceived();

			Properties.Settings.Default.SettingChanging += Default_SettingChanging;
			FileRuntimeTimer.Tick += FileRuntimeTimer_Tick;

			machine.ProbeFinished += Machine_ProbeFinished_UserOutput;

			LoadMacros();

			settingsWindow.SendLine += machine.SendLine;

			machine.Calculator.GetGCode += () => ToolPath;

			CheckBoxUseExpressions_Changed(null, null);
			ButtonRestoreViewport_Click(null, null);

			UpdateCheck.CheckForUpdate();

			if (App.Args.Length > 0)
			{
				if (File.Exists(App.Args[0]))
				{
					openFileDialogGCode.FileName = App.Args[0];
					OpenFileDialogGCode_FileOk(null, null);
				}
			}
		}
        private void StartStream_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlInput.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Please enter a valid MJPEG stream URL.");
                return;
            }

            // Save URL for next time
            Properties.Settings.Default.StreamURL = url;
            Properties.Settings.Default.Save();

            // Stop previous stream if running
            StopStream();

            // Start new stream
            stream = new MJPEGStream(url);
            stream.NewFrame += Stream_NewFrame;
            stream.Start();
        }

        private void Stream_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            try
            {
                using (MemoryStream memory = new MemoryStream())
                {
                    eventArgs.Frame.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
                    memory.Position = 0;

                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = memory;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze(); // Avoid cross-thread issues

                    Dispatcher.Invoke(() => VideoStream.Source = bitmap);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error displaying MJPEG: " + ex.Message);
            }
        }
      

        private void StopStream()
        {
            if (stream != null)
            {
                stream.SignalToStop();
                stream.WaitForStop();
                stream = null;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Console.WriteLine("Window is closing. Stopping stream...");
            StopStream();
            Environment.Exit(0); // Ensure all background threads terminate
        }
        public Vector3 LastProbePosMachine { get; set; }
		public Vector3 LastProbePosWork { get; set; }

		private void Machine_ProbeFinished_UserOutput(Vector3 position, bool success)
		{
			LastProbePosMachine = machine.LastProbePosMachine;
			LastProbePosWork = machine.LastProbePosWork;

			RaisePropertyChanged("LastProbePosMachine");
			RaisePropertyChanged("LastProbePosWork");
		}

		private void UnhandledException(object sender, UnhandledExceptionEventArgs ea)
		{
			Exception e = (Exception)ea.ExceptionObject;

			string info = "Unhandled Exception:\r\nMessage:\r\n";
			info += e.Message;
			info += "\r\nStackTrace:\r\n";
			info += e.StackTrace;
			info += "\r\nToString():\r\n";
			info += e.ToString();

			MessageBox.Show(info);
			Console.WriteLine(info);

			try
			{
				System.IO.File.WriteAllText("OpenCNCPilot_Crash_Log.txt", info);
			}
			catch { }

			Environment.Exit(1);
		}

		private void Default_SettingChanging(object sender, System.Configuration.SettingChangingEventArgs e)
		{
			if (e.SettingName.Equals("JogFeed") ||
				e.SettingName.Equals("JogDistance") ||
				e.SettingName.Equals("ProbeFeed") ||
				e.SettingName.Equals("ProbeSafeHeight") ||
				e.SettingName.Equals("ProbeMinimumHeight") ||
				e.SettingName.Equals("ProbeMaxDepth") ||
				e.SettingName.Equals("SplitSegmentLength") ||
				e.SettingName.Equals("ViewportArcSplit") ||
				e.SettingName.Equals("ArcToLineSegmentLength") ||
				e.SettingName.Equals("ProbeXAxisWeight") ||
				e.SettingName.Equals("ConsoleFadeTime"))
			{
				if (((double)e.NewValue) <= 0)
					e.Cancel = true;
			}

			if (e.SettingName.Equals("SerialPortBaud") ||
				e.SettingName.Equals("StatusPollInterval") ||
				e.SettingName.Equals("ControllerBufferSize"))
			{
				if (((int)e.NewValue) <= 0)
					e.Cancel = true;
			}
		}

		private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
		{
			System.Diagnostics.Process.Start(e.Uri.AbsoluteUri);
		}

		public string Version
		{
			get
			{
				var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
				return $"{version}";
			}
		}

		public string WindowTitle
		{
			get
			{
				if (CurrentFileName.Length < 1)
					return $"OpenCNCPilot v{Version} by martin2250";
				else
					return $"OpenCNCPilot v{Version} by martin2250 - {CurrentFileName}";
			}
		}

		private void Window_Drop(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(DataFormats.FileDrop))
			{
				string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

				if (files.Length > 0)
				{
					string file = files[0];

					if (file.EndsWith(".hmap"))
					{
						if (machine.Mode == Machine.OperatingMode.Probe || Map != null)
							return;

						OpenHeightMap(file);
					}
					else
					{
						if (machine.Mode == Machine.OperatingMode.SendFile)
							return;

						try
						{
							machine.SetFile(System.IO.File.ReadAllLines(file));
						}
						catch (Exception ex)
						{
							MessageBox.Show(ex.Message);
						}
					}
				}
			}
		}

		private void Window_DragEnter(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(DataFormats.FileDrop))
			{
				string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

				if (files.Length > 0)
				{
					string file = files[0];

					if (file.EndsWith(".hmap"))
					{
						if (machine.Mode != Machine.OperatingMode.Probe && Map == null)
						{
							e.Effects = DragDropEffects.Copy;
							return;
						}
					}
					else
					{
						if (machine.Mode != Machine.OperatingMode.SendFile)
						{
							e.Effects = DragDropEffects.Copy;
							return;
						}
					}
				}
			}

			e.Effects = DragDropEffects.None;
		}

		private void viewport_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == System.Windows.Input.Key.Space)
			{
				machine.FeedHold();
				e.Handled = true;
			}
		}

		private void ButtonRapidOverride_Click(object sender, RoutedEventArgs e)
		{
			Button b = sender as Button;

			if (b == null)
				return;

			switch (b.Content as string)
			{
				case "100%":
					machine.SendControl(0x95);
					break;
				case "50%":
					machine.SendControl(0x96);
					break;
				case "25%":
					machine.SendControl(0x97);
					break;
			}
		}

		private void ButtonFeedOverride_Click(object sender, RoutedEventArgs e)
		{
			Button b = sender as Button;

			if (b == null)
				return;

			switch (b.Tag as string)
			{
				case "100%":
					machine.SendControl(0x90);
					break;
				case "+10%":
					machine.SendControl(0x91);
					break;
				case "-10%":
					machine.SendControl(0x92);
					break;
				case "+1%":
					machine.SendControl(0x93);
					break;
				case "-1%":
					machine.SendControl(0x94);
					break;
			}
		}

		private void ButtonSpindleOverride_Click(object sender, RoutedEventArgs e)
		{
			Button b = sender as Button;

			if (b == null)
				return;

			switch (b.Tag as string)
			{
				case "100%":
					machine.SendControl(0x99);
					break;
				case "+10%":
					machine.SendControl(0x9A);
					break;
				case "-10%":
					machine.SendControl(0x9B);
					break;
				case "+1%":
					machine.SendControl(0x9C);
					break;
				case "-1%":
					machine.SendControl(0x9D);
					break;
			}
		}

		private void ButtonResetViewport_Click(object sender, RoutedEventArgs e)
		{
			viewport.Camera.Position = new System.Windows.Media.Media3D.Point3D(50, -150, 250);
			viewport.Camera.LookDirection = new System.Windows.Media.Media3D.Vector3D(-50, 150, -250);
			viewport.Camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
		}

		private void ButtonLayFlatViewport_Click(object sender, RoutedEventArgs e)                  // deHarro, 2024-08-23
        {
			viewport.Camera.Position = new System.Windows.Media.Media3D.Point3D(0, 10, 250);
			viewport.Camera.LookDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, -250);
			viewport.Camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
		}

		private void ButtonRestoreViewport_Click(object sender, RoutedEventArgs e)
		{
			string[] scoords = Properties.Settings.Default.ViewPortPos.Split(';');

			try
			{
				IEnumerable<double> coords = scoords.Select(s => double.Parse(s));

				viewport.Camera.Position = new Vector3(coords.Take(3).ToArray()).ToPoint3D();
				viewport.Camera.LookDirection = new Vector3(coords.Skip(3).ToArray()).ToVector3D();
				viewport.Camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
			}
			catch
			{
				ButtonResetViewport_Click(null, null);
			}
		}

		private void ButtonSaveViewport_Click(object sender, RoutedEventArgs e)
		{
			List<double> coords = new List<double>();

			coords.AddRange(new Vector3(viewport.Camera.Position).Array);
			coords.AddRange(new Vector3(viewport.Camera.LookDirection).Array);

			Properties.Settings.Default.ViewPortPos = string.Join(";", coords.Select(d => d.ToString()));
		}

		private void ButtonSaveTLOPos_Click(object sender, RoutedEventArgs e)
		{
			if (machine.Mode != Machine.OperatingMode.Manual)
				return;

			double Z = (Properties.Settings.Default.TLSUseActualPos) ? machine.MachinePosition.Z : LastProbePosMachine.Z;

			Properties.Settings.Default.ToolLengthSetterPos = Z;
		}

		private void ButtonApplyTLO_Click(object sender, RoutedEventArgs e)
		{
			if (machine.Mode != Machine.OperatingMode.Manual)
				return;

			double Z = (Properties.Settings.Default.TLSUseActualPos) ? machine.MachinePosition.Z : LastProbePosMachine.Z;

			double delta = Z - Properties.Settings.Default.ToolLengthSetterPos;

			machine.SendLine($"G43.1 Z{delta.ToString(Constants.DecimalOutputFormat)}");
		}

		private void ButtonClearTLO_Click(object sender, RoutedEventArgs e)
		{
			if (machine.Mode != Machine.OperatingMode.Manual)
				return;

			machine.SendLine("G49");
		}

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);
			HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
			source.AddHook(WndProc);
		}

		private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if (msg == App.WM_COPYDATA)
			{
				App.COPYDATASTRUCT _dataStruct = Marshal.PtrToStructure<App.COPYDATASTRUCT>(lParam);
				string _strMsg = Marshal.PtrToStringUni(_dataStruct.lpData, _dataStruct.cbData / 2);
				if (File.Exists(_strMsg))
				{
					Activate();
					openFileDialogGCode.FileName = _strMsg;
					OpenFileDialogGCode_FileOk(null, null);
				}
			}

			return IntPtr.Zero;
		}
	}
}
