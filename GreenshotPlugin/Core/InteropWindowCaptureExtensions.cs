﻿#region Dapplo 2017 - GNU Lesser General Public License

// Dapplo - building blocks for .NET applications
// Copyright (C) 2017 Dapplo
// 
// For more information see: http://dapplo.net/
// Dapplo repositories are hosted on GitHub: https://github.com/dapplo
// 
// This file is part of Greenshot
// 
// Greenshot is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Greenshot is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have a copy of the GNU Lesser General Public License
// along with Greenshot. If not, see <http://www.gnu.org/licenses/lgpl.txt>.

#endregion

#region Usings

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Dapplo.Log;
using Dapplo.Windows.App;
using Dapplo.Windows.Desktop;
using Dapplo.Windows.Enums;
using Dapplo.Windows.Native;
using Dapplo.Windows.Structs;
using GreenshotPlugin.Core.Enums;
using GreenshotPlugin.IniFile;
using GreenshotPlugin.Interfaces;

#endregion

namespace GreenshotPlugin.Core
{
	/// <summary>
	///     Greenshot versions of the extension methods for the InteropWindow
	/// </summary>
	public static class InteropWindowCaptureExtensions
	{
		private static readonly LogSource Log = new LogSource();
		private static readonly CoreConfiguration CoreConfiguration = IniConfig.GetIniSection<CoreConfiguration>();
		/// <summary>
		///     Get the file path to the exe for the process which owns this window
		/// </summary>
		public static string GetProcessPath(this IInteropWindow interopWindow)
		{
			int processid = interopWindow.GetProcessId();
			return Kernel32.GetProcessPath(processid);
		}

		/// <summary>
		///     Get the icon belonging to the process
		/// </summary>
		public static Image GetDisplayIcon(this IInteropWindow interopWindow)
		{
			try
			{
				using (var appIcon = User32.GetIcon(interopWindow.Handle, CoreConfiguration.UseLargeIcons))
				{
					if (appIcon != null)
					{
						return appIcon.ToBitmap();
					}
				}
			}
			catch (Exception ex)
			{
				Log.Warn().WriteLine(ex, "Couldn't get icon for window {0} due to: {1}", interopWindow.GetCaption(), ex.Message);
			}
			if (interopWindow.IsApp())
			{
				// No method yet to get the metro icon
				return null;
			}
			try
			{
				return PluginUtils.GetCachedExeIcon(interopWindow.GetProcessPath(), 0);
			}
			catch (Exception ex)
			{
				Log.Warn().WriteLine(ex, "Couldn't get icon for window {0} due to: {1}", interopWindow.GetCaption(), ex.Message);
			}
			return null;
		}

		/// <summary>
		/// Extension method to capture a bitmap of the screen area where the InteropWindow is located
		/// </summary>
		/// <param name="interopWindow">InteropWindow</param>
		/// <param name="clientBounds">true to use the client bounds</param>
		/// <returns>Bitmap</returns>
		public static Bitmap CaptureFromScreen(this IInteropWindow interopWindow, bool clientBounds = false)
		{
			var bounds = clientBounds ? interopWindow.GetClientBounds() : interopWindow.GetBounds();
			return WindowCapture.CaptureRectangle(bounds);
		}

		/// <summary>
		///     Capture Window with GDI+
		/// </summary>
		/// <param name="interopWindow">IInteropWindow</param>
		/// <param name="capture">The capture to fill</param>
		/// <returns>ICapture</returns>
		public static ICapture CaptureGdiWindow(this IInteropWindow interopWindow, ICapture capture)
		{
			var capturedImage = interopWindow.PrintWindow();
			if (capturedImage != null)
			{
				capture.Image = capturedImage;
				capture.Location = interopWindow.GetClientBounds().Location;
				return capture;
			}
			return null;
		}

		/// <summary>
		///     Return an Image representing the Window!
		///     As GDI+ draws it, it will be without Aero borders!
		/// TODO: If there is a parent, this could be removed with SetParent, and set back afterwards.
		/// </summary>
		public static Image PrintWindow(this IInteropWindow nativeWindow)
		{
			var bounds = nativeWindow.GetBounds();
			// Start the capture
			Exception exceptionOccured = null;
			Image returnImage;
			using (var region = nativeWindow.GetRegion())
			{
				var pixelFormat = PixelFormat.Format24bppRgb;
				// Only use 32 bpp ARGB when the window has a region
				if (region != null)
				{
					pixelFormat = PixelFormat.Format32bppArgb;
				}
				returnImage = new Bitmap(bounds.Width, bounds.Height, pixelFormat);
				using (var graphics = Graphics.FromImage(returnImage))
				{
					using (var safeDeviceContext = graphics.GetSafeDeviceContext())
					{
						var printSucceeded = User32.PrintWindow(nativeWindow.Handle, safeDeviceContext.DangerousGetHandle(), 0x0);
						if (!printSucceeded)
						{
							// something went wrong, most likely a "0x80004005" (Acess Denied) when using UAC
							exceptionOccured = User32.CreateWin32Exception("PrintWindow");
						}
					}

					// Apply the region "transparency"
					if (region != null && !region.IsEmpty(graphics))
					{
						graphics.ExcludeClip(region);
						graphics.Clear(Color.Transparent);
					}

					graphics.Flush();
				}
			}

			// Return null if error
			if (exceptionOccured != null)
			{
				Log.Error().WriteLine("Error calling print window: {0}", exceptionOccured.Message);
				returnImage.Dispose();
				return null;
			}
			if (!nativeWindow.HasParent && nativeWindow.IsMaximized())
			{
				Log.Debug().WriteLine("Correcting for maximalization");
				Size borderSize = nativeWindow.GetBorderSize();
				var borderRectangle = new Rectangle(borderSize.Width, borderSize.Height, bounds.Width - 2 * borderSize.Width, bounds.Height - 2 * borderSize.Height);
				ImageHelper.Crop(ref returnImage, ref borderRectangle);
			}
			return returnImage;
		}

		/// <summary>
		///     Capture DWM Window
		/// </summary>
		/// <param name="interopWindow">IInteropWindow</param>
		/// <param name="capture">Capture to fill</param>
		/// <param name="windowCaptureMode">Wanted WindowCaptureModes</param>
		/// <param name="autoMode">True if auto modus is used</param>
		/// <returns>ICapture with the capture</returns>
		public static ICapture CaptureDwmWindow(this IInteropWindow interopWindow, ICapture capture, WindowCaptureModes windowCaptureMode, bool autoMode)
		{
			var thumbnailHandle = IntPtr.Zero;
			Form tempForm = null;
			var tempFormShown = false;
			try
			{
				tempForm = new Form
				{
					ShowInTaskbar = false,
					FormBorderStyle = FormBorderStyle.None,
					TopMost = true
				};

				// Register the Thumbnail
				Dwm.DwmRegisterThumbnail(tempForm.Handle, interopWindow.Handle, out thumbnailHandle);

				// Get the original size
				SIZE sourceSize;
				Dwm.DwmQueryThumbnailSourceSize(thumbnailHandle, out sourceSize);

				if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
				{
					return null;
				}

				// Calculate the location of the temp form
				var windowRectangle = interopWindow.GetBounds();
				var formLocation = windowRectangle.Location;
				var borderSize = new Size();
				var doesCaptureFit = false;
				if (!interopWindow.IsMaximized())
				{
					// Assume using it's own location
					formLocation = windowRectangle.Location;
					using (var workingArea = new Region(Screen.PrimaryScreen.Bounds))
					{
						// Find the screen where the window is and check if it fits
						foreach (var screen in Screen.AllScreens)
						{
							if (!Equals(screen, Screen.PrimaryScreen))
							{
								workingArea.Union(screen.Bounds);
							}
						}

						// If the formLocation is not inside the visible area
						if (!workingArea.AreRectangleCornersVisisble(windowRectangle))
						{
							// If none found we find the biggest screen
							foreach (var screen in Screen.AllScreens)
							{
								var newWindowRectangle = new Rectangle(screen.WorkingArea.Location, windowRectangle.Size);
								if (workingArea.AreRectangleCornersVisisble(newWindowRectangle))
								{
									formLocation = screen.Bounds.Location;
									doesCaptureFit = true;
									break;
								}
							}
						}
						else
						{
							doesCaptureFit = true;
						}
					}
				}
				else if (!Environment.OSVersion.IsWindows8OrLater())
				{
					//GetClientRect(out windowRectangle);
					borderSize = interopWindow.GetBorderSize();
					formLocation = new Point(windowRectangle.X - borderSize.Width, windowRectangle.Y - borderSize.Height);
				}

				tempForm.Location = formLocation;
				tempForm.Size = sourceSize;

				// Prepare rectangle to capture from the screen.
				var captureRectangle = new Rectangle(formLocation.X, formLocation.Y, sourceSize.Width, sourceSize.Height);
				if (interopWindow.IsMaximized())
				{
					// Correct capture size for maximized window by offsetting the X,Y with the border size
					// and subtracting the border from the size (2 times, as we move right/down for the capture without resizing)
					captureRectangle.Inflate(borderSize.Width, borderSize.Height);
				}
				else
				{
					// TODO: Also 8.x?
					if (Environment.OSVersion.IsWindows10())
					{
						captureRectangle.Inflate(CoreConfiguration.Win10BorderCrop);
					}

					if (autoMode)
					{
						// check if the capture fits
						if (!doesCaptureFit)
						{
							// if GDI is allowed.. (a screenshot won't be better than we comes if we continue)
							using (var thisWindowProcess = Process.GetProcessById(interopWindow.GetProcessId()))
							{
								if (!interopWindow.IsApp() && WindowCapture.IsGdiAllowed(thisWindowProcess))
								{
									// we return null which causes the capturing code to try another method.
									return null;
								}
							}
						}
					}
				}
				// Prepare the displaying of the Thumbnail
				var props = new DwmThumbnailProperties
				{
					Opacity = 255,
					Visible = true,
					Destination = new RECT(0, 0, sourceSize.Width, sourceSize.Height)
				};
				Dwm.DwmUpdateThumbnailProperties(thumbnailHandle, ref props);
				tempForm.Show();
				tempFormShown = true;

				// Intersect with screen
				captureRectangle.Intersect(capture.ScreenBounds);

				// Destination bitmap for the capture
				Bitmap capturedBitmap = null;
				// Check if we make a transparent capture
				if (windowCaptureMode == WindowCaptureModes.AeroTransparent)
				{
					// Use white, later black to capture transparent
					tempForm.BackColor = Color.White;
					// Make sure everything is visible
					tempForm.Refresh();
					Application.DoEvents();

					try
					{
						using (var whiteBitmap = WindowCapture.CaptureRectangle(captureRectangle))
						{
							// Apply a white color
							tempForm.BackColor = Color.Black;
							// Make sure everything is visible
							tempForm.Refresh();
							if (!interopWindow.IsApp())
							{
								// Make sure the application window is active, so the colors & buttons are right
								// TODO: Await?
								interopWindow.ToForegroundAsync();
							}
							// Make sure all changes are processed and visible
							Application.DoEvents();
							using (var blackBitmap = WindowCapture.CaptureRectangle(captureRectangle))
							{
								capturedBitmap = ApplyTransparency(blackBitmap, whiteBitmap);
							}
						}
					}
					catch (Exception e)
					{
						Log.Warn().WriteLine(e, "Exception: ");
						// Some problem occurred, cleanup and make a normal capture
						if (capturedBitmap != null)
						{
							capturedBitmap.Dispose();
							capturedBitmap = null;
						}
					}
				}
				// If no capture up till now, create a normal capture.
				if (capturedBitmap == null)
				{
					// Remove transparency, this will break the capturing
					if (!autoMode)
					{
						tempForm.BackColor = Color.FromArgb(255, CoreConfiguration.DWMBackgroundColor.R, CoreConfiguration.DWMBackgroundColor.G, CoreConfiguration.DWMBackgroundColor.B);
					}
					else
					{
						var colorizationColor = Dwm.ColorizationSystemDrawingColor;
						// Modify by losing the transparency and increasing the intensity (as if the background color is white)
						colorizationColor = Color.FromArgb(255, (colorizationColor.R + 255) >> 1, (colorizationColor.G + 255) >> 1, (colorizationColor.B + 255) >> 1);
						tempForm.BackColor = colorizationColor;
					}
					// Make sure everything is visible
					tempForm.Refresh();
					if (!interopWindow.IsApp())
					{
						// Make sure the application window is active, so the colors & buttons are right
						interopWindow.ToForegroundAsync();
					}
					// Make sure all changes are processed and visible
					Application.DoEvents();
					// Capture from the screen
					capturedBitmap = WindowCapture.CaptureRectangle(captureRectangle);
				}
				if (capturedBitmap != null)
				{
					// Not needed for Windows 8
					if (!Environment.OSVersion.IsWindows8OrLater())
					{
						// Only if the Inivalue is set, not maximized and it's not a tool window.
						if (CoreConfiguration.WindowCaptureRemoveCorners && !interopWindow.IsMaximized() && (interopWindow.GetExtendedStyle() & ExtendedWindowStyleFlags.WS_EX_TOOLWINDOW) == 0)
						{
							// Remove corners
							if (!Image.IsAlphaPixelFormat(capturedBitmap.PixelFormat))
							{
								Log.Debug().WriteLine("Changing pixelformat to Alpha for the RemoveCorners");
								var tmpBitmap = capturedBitmap.CloneImage(PixelFormat.Format32bppArgb) as Bitmap;
								capturedBitmap.Dispose();
								capturedBitmap = tmpBitmap;
							}
							RemoveCorners(capturedBitmap);
						}
					}
				}

				capture.Image = capturedBitmap;
				// Make sure the capture location is the location of the window, not the copy
				capture.Location = interopWindow.GetBounds().Location;
			}
			finally
			{
				if (thumbnailHandle != IntPtr.Zero)
				{
					// Unregister (cleanup), as we are finished we don't need the form or the thumbnail anymore
					Dwm.DwmUnregisterThumbnail(thumbnailHandle);
				}
				if (tempForm != null)
				{
					if (tempFormShown)
					{
						tempForm.Close();
					}
					tempForm.Dispose();
					tempForm = null;
				}
			}

			return capture;
		}

		/// <summary>
		///     Helper method to remove the corners from a DMW capture
		/// </summary>
		/// <param name="image">The bitmap to remove the corners from.</param>
		private static void RemoveCorners(Bitmap image)
		{
			using (var fastBitmap = FastBitmap.Create(image))
			{
				for (var y = 0; y < CoreConfiguration.WindowCornerCutShape.Count; y++)
				{
					for (var x = 0; x < CoreConfiguration.WindowCornerCutShape[y]; x++)
					{
						fastBitmap.SetColorAt(x, y, Color.Transparent);
						fastBitmap.SetColorAt(image.Width - 1 - x, y, Color.Transparent);
						fastBitmap.SetColorAt(image.Width - 1 - x, image.Height - 1 - y, Color.Transparent);
						fastBitmap.SetColorAt(x, image.Height - 1 - y, Color.Transparent);
					}
				}
			}
		}

		/// <summary>
		///     Apply transparency by comparing a transparent capture with a black and white background
		///     A "Math.min" makes sure there is no overflow, but this could cause the picture to have shifted colors.
		///     The pictures should have been taken without differency, except for the colors.
		/// </summary>
		/// <param name="blackBitmap">Bitmap with the black image</param>
		/// <param name="whiteBitmap">Bitmap with the black image</param>
		/// <returns>Bitmap with transparency</returns>
		private static Bitmap ApplyTransparency(Bitmap blackBitmap, Bitmap whiteBitmap)
		{
			using (var targetBuffer = FastBitmap.CreateEmpty(blackBitmap.Size, PixelFormat.Format32bppArgb, Color.Transparent))
			{
				targetBuffer.SetResolution(blackBitmap.HorizontalResolution, blackBitmap.VerticalResolution);
				using (var blackBuffer = FastBitmap.Create(blackBitmap))
				{
					using (var whiteBuffer = FastBitmap.Create(whiteBitmap))
					{
						for (var y = 0; y < blackBuffer.Height; y++)
						{
							for (var x = 0; x < blackBuffer.Width; x++)
							{
								var c0 = blackBuffer.GetColorAt(x, y);
								var c1 = whiteBuffer.GetColorAt(x, y);
								// Calculate alpha as double in range 0-1
								var alpha = c0.R - c1.R + 255;
								if (alpha == 255)
								{
									// Alpha == 255 means no change!
									targetBuffer.SetColorAt(x, y, c0);
								}
								else if (alpha == 0)
								{
									// Complete transparency, use transparent pixel
									targetBuffer.SetColorAt(x, y, Color.Transparent);
								}
								else
								{
									// Calculate original color
									var originalAlpha = (byte)Math.Min(255, alpha);
									var alphaFactor = alpha / 255d;
									//LOG.DebugFormat("Alpha {0} & c0 {1} & c1 {2}", alpha, c0, c1);
									var originalRed = (byte)Math.Min(255, c0.R / alphaFactor);
									var originalGreen = (byte)Math.Min(255, c0.G / alphaFactor);
									var originalBlue = (byte)Math.Min(255, c0.B / alphaFactor);
									var originalColor = Color.FromArgb(originalAlpha, originalRed, originalGreen, originalBlue);
									//Color originalColor = Color.FromArgb(originalAlpha, originalRed, c0.G, c0.B);
									targetBuffer.SetColorAt(x, y, originalColor);
								}
							}
						}
					}
				}
				return targetBuffer.UnlockAndReturnBitmap();
			}
		}
	}
}