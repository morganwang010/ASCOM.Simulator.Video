﻿//tabs=4
// --------------------------------------------------------------------------------
//
// ASCOM Video Driver - Video Client
//
// Description:	The main form of the Video Client
//
// Author:		(HDP) Hristo Pavlov <hristo_dpavlov@yahoo.com>
//
// --------------------------------------------------------------------------------
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ASCOM.DeviceInterface;
using ASCOM.DriverAccess;
using ASCOM.Utilities.Video;
using Client.Properties;

namespace Client
{
	public partial class frmMain : Form
	{
		private static string VIDEO_DEVICE_TYPE = "Video";
		private static string VIDEO_DRIVER_NAME = "Tangra.DirectShow.Video";

		private IVideoWrapper videoObject;
		private bool running = false;

		private int imageWidth;
		private int imageHeight;
		private string recordingfileName;
		private int framesBeforeUpdatingCameraVideoFormat = -1;

	    private ICameraImage cameraImage;

		public frmMain()
		{
			InitializeComponent();

			running = true;
			previewOn = cbFrameFetcher.Checked;

		    cameraImage = new CameraImage();

			ThreadPool.QueueUserWorkItem(new WaitCallback(DisplayVideoFrames));
		}

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose(bool disposing)
		{
			if (disposing && (components != null))
			{
				components.Dispose();
			}
			base.Dispose(disposing);

			DisconnectFromCamera();
			running = false;
		}

		private void ConnectToCamera()
		{
			var chooser = new ASCOM.Utilities.Chooser();
			chooser.DeviceType = VIDEO_DEVICE_TYPE;
			string progId = chooser.Choose(VIDEO_DRIVER_NAME);

			if (!string.IsNullOrEmpty(progId))
			{
				videoObject = VideoWrapper.CreateVideoWrapper(progId);
				
				try
				{
					Cursor = Cursors.WaitCursor;
					videoObject.Connected = true;

					if (videoObject.Connected)
					{
						if (videoObject.SupporstFreeStyleGain)
							videoObject.SetFreeRangeGainIntervals(Settings.Default.FreeRangeGainIntervals);

						imageWidth = videoObject.Width;
						imageHeight = videoObject.Height;
						pictureBox.Image = new Bitmap(imageWidth, imageHeight);

						ResizeVideoFrameTo(imageWidth, imageHeight);
					}
				}
				finally
				{
					Cursor = Cursors.Default;
				}


				pictureBox.Width = videoObject.Width;
				pictureBox.Height = videoObject.Height;

				UpdateCameraState(true);
			}
		}

		private void DisconnectFromCamera()
		{
			if (videoObject != null)
			{
				videoObject.Disconnect();
				videoObject = null;
			}

			UpdateCameraState(false);
		}

		private void UpdateCameraState(bool connected)
		{
			pnlVideoControls.Enabled = connected;
			miConnect.Enabled = !connected;
			miDisconnect.Enabled = connected;
			miCameraInfo.Enabled = connected;
			miDriverInfo.Enabled = connected;
			miSettings.Enabled = !connected;
			
			miActions.DropDownItems.Clear();

			if (connected)
			{
				if (videoObject != null &&
					videoObject.HasSupportedActions)
				{

					foreach (string action in videoObject.SupportedActions)
					{
						ToolStripItem tsi = miActions.DropDownItems.Add(action);
						tsi.Tag = action;
						tsi.Click += new EventHandler(tsi_Click);
					}
				}
			}

			miActions.Enabled = 
				connected && 
				videoObject != null &&
				videoObject.HasSupportedActions;

			miVideoSetup.Enabled = connected && videoObject != null;

			UpdateState();

			pnlVideoControls.Enabled = connected;
			btnRecord.Enabled = connected && videoObject != null && videoObject.State == VideoCameraState.videoCameraRunning;
			btnStopRecording.Enabled = connected && videoObject != null && videoObject.State == VideoCameraState.videoCameraRecording;
			btnImageSettings.Enabled = connected && videoObject != null && videoObject.CanConfigureDeviceProperties;

			if (videoObject != null)
			{
				lblCameraType.Text = videoObject.CameraFrameRate;
				lblBitDepth.Text = videoObject.CameraBitDepth;
				lblVideoFormat.Text = videoObject.CameraVideoFormat;
				lblBuffering.Text = videoObject.BufferingInfo;

				Text = string.Format("ASCOM Video Client - {0}{1}", 
						videoObject.DeviceName, 
						videoObject.VideoCaptureDeviceName != null
							? string.Format(" ({0})", videoObject.VideoCaptureDeviceName) 
							: string.Empty);
			}
			else
			{
				lblCameraType.Text = "N/A";
				lblBitDepth.Text = "N/A";
				lblVideoFormat.Text = "N/A";
				lblBuffering.Text = "N/A";
				Text = "ASCOM Video Client";
			}
		}

		void tsi_Click(object sender, EventArgs e)
		{
			ToolStripItem tsi = sender as ToolStripItem;
			if (tsi != null)
			{
				var frm = new frmRunAction();
				string actionName = (string) tsi.Tag;

				frm.SetActionName(actionName);

				if (frm.ShowDialog(this) == DialogResult.OK)
				{
					string paramValue = frm.GetParameterValue();

					string actionResult = videoObject.ExecuteAction(actionName, paramValue);

					// TODO: Output the result in the log pane
				}
			}
		}

		private void exitToolStripMenuItem_Click(object sender, EventArgs e)
		{
			DisconnectFromCamera();

			Close();
		}

		private void miConfigure_Click(object sender, EventArgs e)
		{
			var frmSettings = new frmSettings();
			if (frmSettings.ShowDialog(this) == DialogResult.OK)
			{
				if (videoObject != null)
				{
					videoObject.SetFreeRangeGainIntervals(Settings.Default.FreeRangeGainIntervals);	
				}
			}
		}

		private void miConnect_Click(object sender, EventArgs e)
		{
			ConnectToCamera();
		}

		private void miDisconnect_Click(object sender, EventArgs e)
		{
			DisconnectFromCamera();
		}

		private static Font debugTextFont = new Font(FontFamily.GenericMonospace, 10);

		private long lastDisplayedVideoFrameNumber = -1;
		private bool previewOn = true;

		private delegate void PaintVideoFrameDelegate(IVideoFrame frame, Bitmap bmp);

		private int renderedFrameCounter = 0;
		private long startTicks = 0;
		private long endTicks = 0;

		private double renderFps = double.NaN;
		private long currentFrameNo = 0;

		private void PaintVideoFrame(IVideoFrame frame, Bitmap bmp)
		{
			bool isEmptyFrame = frame == null;
			if (!isEmptyFrame)
				isEmptyFrame = frame.ImageArray == null;

			if (isEmptyFrame)
			{
				using (Graphics g = Graphics.FromImage(pictureBox.Image))
				{
					if (bmp == null)
						g.Clear(Color.Green);
					else
						g.DrawImage(bmp, 0, 0);

					g.Save();
				}

				pictureBox.Invalidate();
				return;
			}

			currentFrameNo = frame.FrameNumber;
			UpdateState();
			renderedFrameCounter++;

			if (renderedFrameCounter == 20)
			{
				renderedFrameCounter = 0;
				endTicks = DateTime.Now.Ticks;
				if (startTicks != 0)
				{
					renderFps = 20.0 / new TimeSpan(endTicks - startTicks).TotalSeconds;
				}
				startTicks = DateTime.Now.Ticks;
			}

			using (Graphics g = Graphics.FromImage(pictureBox.Image))
			{
			    g.DrawImage(bmp, 0, 0);

			    g.Save();
			}

			pictureBox.Invalidate();
			bmp.Dispose();

			if (framesBeforeUpdatingCameraVideoFormat >= 0)
				framesBeforeUpdatingCameraVideoFormat--;

			if (framesBeforeUpdatingCameraVideoFormat == 0)
			{
				lblVideoFormat.Text = videoObject.CameraVideoFormat;
			}
		}
		
		internal class FakeFrame : IVideoFrame
		{
			private static int s_Counter = -1;

			public object ImageArray
			{
				get { return new object(); }
			}

			public object ImageArrayVariant
			{
				get { return null; }
			}

			public byte[] PreviewBitmap
			{
				get { return null; }
			}

			public long FrameNumber
			{
				get
				{
					s_Counter++;
					return s_Counter;
				}
			}

			public double ExposureDuration
			{
				get { return 0; }
			}

			public string ExposureStartTime
			{
				get { return null; }
			}

			public ArrayList ImageMetadata
			{
				get { return new ArrayList(); }
			}
		}


		private void DisplayVideoFrames(object state)
		{
			while(running)
			{
				if (videoObject != null &&
					videoObject.IsConnected &&
					previewOn)
				{
					try
					{
						IVideoFrame frame = videoObject.LastVideoFrame;

						if (frame != null &&
							(frame.FrameNumber == -1 || frame.FrameNumber != lastDisplayedVideoFrameNumber))
						{
							lastDisplayedVideoFrameNumber = frame.FrameNumber;

							Bitmap bmp = null;

							if (Settings.Default.UsePreviewBitmap)
							{
								using (var memStr = new MemoryStream(frame.PreviewBitmap))
								{
									bmp = (Bitmap)Image.FromStream(memStr);	
								}
								
							}
							else if (Settings.Default.UseNativeCode)
							{
                                cameraImage.SetImageArray(
									frame.ImageArray, 
                                    imageWidth, 
                                    imageHeight, 
                                    videoObject.SensorType);

							    byte[] bmpBytes = cameraImage.GetDisplayBitmapBytes();
								using (MemoryStream memStr = new MemoryStream(bmpBytes))
								{
									bmp = (Bitmap)Image.FromStream(memStr);
								}
							}
							else
							{
								Array safeArr = (Array) frame.ImageArray;

								int[,] pixels;
								if (safeArr is int[,])
									pixels = (int[,])safeArr;
								else if (safeArr is int[,,])
								{
									// R,G,B planes
									throw new NotSupportedException();
								}
								else
									throw new NotSupportedException("Unsupported pixel format in Managed mode.");

								bmp = new Bitmap(imageWidth, imageHeight);
								BitmapData bmData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
								try
								{
									unsafe
									{
										int stride = bmData.Stride;
										IntPtr Scan0 = bmData.Scan0;
										byte* p = (byte*)(void*)Scan0;

										int nOffset = stride - bmp.Width * 3;

										for (int y = 0; y < bmp.Height; ++y)
										{
											for (int x = 0; x < bmp.Width; ++x)
											{
												byte red = (byte)pixels[y, x];
												p[0] = red;
												p[1] = red;
												p[2] = red;

												p += 3;
											}
											p += nOffset;
										}
									}
								}
								finally
								{
									bmp.UnlockBits(bmData);
								}
							}

							Invoke(new PaintVideoFrameDelegate(PaintVideoFrame), new object[] { frame, bmp });
						}
					}
					catch(ObjectDisposedException){ }
					catch(Exception ex)
					{
						Trace.WriteLine(ex);

						Bitmap errorBmp = new Bitmap(pictureBox.Width, pictureBox.Height);
						using (Graphics g = Graphics.FromImage(errorBmp))
						{
							g.Clear(Color.Tomato);
							g.DrawString(ex.Message, debugTextFont, Brushes.Black, 10, 10);
							g.Save();
						}
						try
						{
							Invoke(new PaintVideoFrameDelegate(PaintVideoFrame), new object[] {null, errorBmp});
						}
						catch(InvalidOperationException)
						{
							// InvalidOperationException could be thrown when closing down the app i.e. when the form has been already disposed
						}
					}

				}

				Thread.Sleep(1);
				Application.DoEvents();
			}
		}

		private void cbFrameFetcher_CheckedChanged(object sender, EventArgs e)
		{
			previewOn = cbFrameFetcher.Checked;
		}

		private void UpdateState()
		{
			if (videoObject == null)
			{
				tssCameraState.Text = "Disconnected";
				tssFrameNo.Text = string.Empty;
				tssDisplayRate.Text = string.Empty;
				tssFrameNo.Visible = false;
				tssDisplayRate.Visible = false;
			}
			else
			{
				switch(videoObject.State)
				{
					case VideoCameraState.videoCameraRunning:
						tssCameraState.Text = "Running";
						break;

					case VideoCameraState.videoCameraRecording:
						tssCameraState.Text = "Recording";
						break;

					case VideoCameraState.videoCameraError:
						tssCameraState.Text = "Error";
						break;
				}

				if (!tssFrameNo.Visible) tssFrameNo.Visible = true;				

				tssFrameNo.Text = currentFrameNo.ToString("Current Frame: 0", CultureInfo.InvariantCulture);
				if (!double.IsNaN(renderFps))
				{
					if (!tssDisplayRate.Visible) tssDisplayRate.Visible = true;
					tssDisplayRate.Text = renderFps.ToString("Display Rate: 0.00 fps");
				}
				else
					tssDisplayRate.Text = "Display Rate: N/A";

				lblGamma.Text = videoObject.Gamma;
				btnGammaUp.Enabled = videoObject.CanIncreaseGamma;
				btnGammaDown.Enabled = videoObject.CanDecreaseGamma;

				lblGain.Text = videoObject.Gain;
				btnGainUp.Enabled = videoObject.CanIncreaseGain;
				btnGainDown.Enabled = videoObject.CanDecreaseGain;

				lblIntegration.Text = videoObject.Integration;
				btnIntegrationUp.Enabled = videoObject.CanIncreaseIntegration;
				btnIntegrationDown.Enabled = videoObject.CanDecreaseIntegration;

				if (videoObject.State == VideoCameraState.videoCameraRecording)
				{
					var fi = new FileInfo(recordingfileName);
					tssRecordingFile.Text = string.Format("{0} ({1} Mb)", fi.Name, fi.Length / (1024 * 1024));

					tssRecordingFile.Visible = true;
					btnStopRecording.Enabled = true;
					btnRecord.Enabled = false;
				}
				else
				{
					tssRecordingFile.Visible = false;
					btnStopRecording.Enabled = false;
					btnRecord.Enabled = true;
				}
			}
		}

		private void btnRecord_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
			{
				string fileName = Path.GetFullPath(string.Format("{0}\\video-{1}.avi", Settings.Default.OutputLocation, DateTime.Now.ToString("yyyy-MMM-dd HH-mm-ss")));

				recordingfileName = videoObject.StartRecording(fileName);

				UpdateState();

				framesBeforeUpdatingCameraVideoFormat = 4;
			}
		}

		private void btnStopRecording_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
			{
				videoObject.StopRecording();

				UpdateState();
			}
		}

		private void btnGammaUp_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
				videoObject.IncreaseGamma();
		}

		private void btnGammaDown_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
				videoObject.DecreaseGamma();
		}

		private void btnGainDown_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
				videoObject.DecreaseGain();
		}

		private void btnGainUp_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
				videoObject.IncreaseGain();
		}

		private void btnIntegrationDown_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
				videoObject.DecreaseIntegration();
		}

		private void btnIntegrationUp_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
				videoObject.IncreaseIntegration();
		}

		private void miDriverInfo_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
			{
				var frm = new frmDriverInfo();
				frm.SetVideoObject(videoObject);

				frm.ShowDialog(this);
			}
		}

		private void miCameraInfo_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
			{
				var frm = new frmCameraInfo();
				frm.SetVideoObject(videoObject);

				frm.ShowDialog(this);
			}
		}

		private void ResizeVideoFrameTo(int imageWidth, int imageHeight)
		{
			Width = Math.Max(800, (imageWidth - pictureBox.Width) + this.Width);
			Height = Math.Max(600, (imageHeight - pictureBox.Height) + this.Height);
		}

		private void btnImageSettings_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
				videoObject.ConfigureDeviceProperties();
		}

		private void miVideoSetup_Click(object sender, EventArgs e)
		{
			if (videoObject != null)
				videoObject.SetupDialog();
		}
	}
}
