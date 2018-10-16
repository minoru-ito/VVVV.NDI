using System;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

using VVVV.Core.Logging;
using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;

using NewTek;
using NewTek.NDI;

namespace VVVV.DX11.Nodes
{
    namespace VVVVNDI
    {
        [PluginInfo(Name = "NDISendTest", Category = "NDI", AutoEvaluate = true)]
        public class NDISendTestNode : IPluginEvaluate, IPartImportsSatisfiedNotification, IDisposable
        {
            [Input("Send", IsBang = true)]
            ISpread<bool> FInSend;

            [Input("Update", IsBang = true)]
            ISpread<bool> FInUpdate;

            [Input("Timeout", MinValue = 0, DefaultValue = 1000)]
            ISpread<uint> FInTimeout;

            //[Output("Output")]
            //ISpread<int> FOut;

            [Import()]
            public ILogger FLogger;

            IntPtr sourceNamePtr;
            IntPtr groupsNamePtr;
            IntPtr sendInstancePtr;
            IntPtr bufferPtr;
            NDIlib.video_frame_v2_t videoFrame;
            Bitmap bmp;
            Graphics graphics;
            StringFormat textFormat;
            FontFamily fontFamily;
            Pen outlinePen;
            Pen thinOutlinePen;
            int frameNumber;

            bool disposed = false;

            static string DrawPrettyText(Graphics graphics, String text, float size, FontFamily family, Point origin, StringFormat format, Brush fill, Pen outline)
            {
                try
                {
                    // make a text path
                    GraphicsPath path = new GraphicsPath();
                    path.AddString(text, family, 0, size, origin, format);

                    // Draw the pretty text
                    graphics.FillPath(fill, path);
                    graphics.DrawPath(outline, path);

                    return "";
                }
                catch (Exception e)
                {
                    return e.Message;
                }
            }

            public void OnImportsSatisfied()
            {
                frameNumber = 0;

                // .Net interop doesn't handle UTF-8 strings, so do it manually
                // These must be freed later
                sourceNamePtr = UTF.StringToUtf8("Example");

                groupsNamePtr = IntPtr.Zero;

                // Not required, but "correct". (see the SDK documentation)
                if (!NDIlib.initialize())
                {
                    // Cannot run NDI. Most likely because the CPU is not sufficient (see SDK documentation).
                    // you can check this directly with a call to NDIlib_is_supported_CPU()
                    FLogger.Log(LogType.Error, "Cannot run NDI");
                    return;
                }
                else
                {
                    FLogger.Log(LogType.Message, "NDI initialized");
                }

                // Create an NDI source description using sourceNamePtr and it's clocked to the video.
                NDIlib.send_create_t createDesc = new NDIlib.send_create_t()
                {
                    p_ndi_name = sourceNamePtr,
                    p_groups = groupsNamePtr,
                    clock_video = true,
                    clock_audio = false
                };

                // We create the NDI finder instance
                sendInstancePtr = NDIlib.send_create(ref createDesc);

                // free the strings we allocated
                Marshal.FreeHGlobal(sourceNamePtr);
                Marshal.FreeHGlobal(groupsNamePtr);

                // did it succeed?
                if (sendInstancePtr == IntPtr.Zero)
                {
                    FLogger.Log(LogType.Error, "Failed to create send instance");
                    //Console.WriteLine("Failed to create send instance");
                    return;
                }
                else
                {
                    FLogger.Log(LogType.Message, "Successed to create send instance");
                }

                // define our bitmap properties
                int xres = 1920;
                int yres = 1080;
                int stride = (xres * 32/*BGRA bpp*/ + 7) / 8;
                int bufferSize = yres * stride;

                // allocate some memory for a video buffer
                bufferPtr = Marshal.AllocHGlobal((int)bufferSize);

                // We are going to create a 1920x1080 progressive frame at 29.97Hz.
                videoFrame = new NDIlib.video_frame_v2_t()
                {
                    // Resolution
                    xres = xres,
                    yres = yres,
                    // Use BGRA video
                    FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
                    // The frame-eate
                    frame_rate_N = 120000,//30000,
                    frame_rate_D = 1000,//1001,
                    // The aspect ratio (16:9)
                    picture_aspect_ratio = (16.0f / 9.0f),
                    // This is a progressive frame
                    frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                    // Timecode.
                    timecode = NDIlib.send_timecode_synthesize,
                    // The video memory used for this frame
                    p_data = bufferPtr,
                    // The line to line stride of this image
                    line_stride_in_bytes = stride,
                    // no metadata
                    p_metadata = IntPtr.Zero,
                    // only valid on received frames
                    timestamp = 0//NDIlib.recv_timestamp_undefined
                };

                // get a compatible bitmap and graphics context
                bmp = new Bitmap((int)xres, (int)yres, (int)stride, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bufferPtr);
                graphics = Graphics.FromImage(bmp);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                // We'll use these later inside the loop
                textFormat = new StringFormat();
                textFormat.Alignment = StringAlignment.Center;
                textFormat.LineAlignment = StringAlignment.Center;

                fontFamily = new FontFamily("Arial");
                outlinePen = new Pen(Color.Black, 2.0f);
                thinOutlinePen = new Pen(Color.Black, 1.0f);
            }

            public void Dispose()
            {
                Dispose(true);
            }

            private void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (!disposed)
                    {
                        disposed = true;

                        // Dispose of our graphics resources
                        graphics.Dispose();
                        bmp.Dispose();

                        // free our buffer
                        Marshal.FreeHGlobal(bufferPtr);

                        // Destroy the NDI sender
                        NDIlib.send_destroy(sendInstancePtr);

                        // Not required, but "correct". (see the SDK documentation)
                        NDIlib.destroy();
                    }
                }
            }

            public void Evaluate(int SpreadMax)
            {
                //FLogger.Log(LogType.Debug, "Evaluate");

                //FOut.SliceCount = FIn.SliceCount;

                //for (int i = 0; i < SpreadMax; i++)
                //{
                //    FOut[i] = FIn[i] * 2;
                //}

                if (FInUpdate[0])
                {
                    string msg = "";
                    frameNumber++;

                    // fill it with a lovely color
                    graphics.Clear(Color.Maroon);

                    // show which source we are
                    msg = DrawPrettyText(graphics, "C# Example Source", 96.0f, fontFamily, new Point(960, 100), textFormat, Brushes.White, outlinePen);
                    if (msg != "")
                        FLogger.Log(LogType.Error, "DrawPrettyText error: " + msg);

                    try
                    {
                        // Get the tally state of this source (we poll it),
                        NDIlib.tally_t NDI_tally = new NDIlib.tally_t();
                        NDIlib.send_get_tally(sendInstancePtr, ref NDI_tally, 0);

                        // Do something different depending on where we are shown
                        if (NDI_tally.on_program)
                        {
                            msg = DrawPrettyText(graphics, "On Program", 96.0f, fontFamily, new Point(960, 225), textFormat, Brushes.White, outlinePen);
                            if (msg != "")
                                FLogger.Log(LogType.Error, "DrawPrettyText error: " + msg);
                        }
                        else if (NDI_tally.on_preview)
                        {
                            msg = DrawPrettyText(graphics, "On Preview", 96.0f, fontFamily, new Point(960, 225), textFormat, Brushes.White, outlinePen);
                            if (msg != "")
                                FLogger.Log(LogType.Error, "DrawPrettyText error: " + msg);
                        }
                    }
                    catch (Exception e)
                    {
                        FLogger.Log(LogType.Error, "Update: " + e.Message);
                    }


                    //// show what frame we've rendered
                    msg = DrawPrettyText(graphics, String.Format("Frame {0}", frameNumber.ToString()), 96.0f, fontFamily, new Point(960, 350), textFormat, Brushes.White, outlinePen);
                    if (msg != "")
                        FLogger.Log(LogType.Error, "DrawPrettyText error: " + msg);

                    // show current time
                    msg = DrawPrettyText(graphics, System.DateTime.Now.ToString(), 96.0f, fontFamily, new Point(960, 900), textFormat, Brushes.White, outlinePen);
                    if (msg != "")
                        FLogger.Log(LogType.Error, "DrawPrettyText error: " + msg);
                }

                if (FInSend[0])
                {
                    // are we connected to anyone?
                    //if (NDI.Send.NDIlib_send_get_no_connections(sendInstancePtr, 10000) < 1)
                    if (NDIlib.send_get_no_connections(sendInstancePtr, FInTimeout[0]) < 1)
                    {
                        // no point rendering
                        FLogger.Log(LogType.Debug, "No current connections, so no rendering needed.");
                        //Console.WriteLine("No current connections, so no rendering needed.");

                        // Wait a bit, otherwise our limited example will end before you can connect to it
                        System.Threading.Thread.Sleep(50);
                    }
                    else
                    {
                        /*
                        // fill it with a lovely color
                        graphics.Clear(Color.Maroon);

                        // show which source we are
                        DrawPrettyText(graphics, "C# Example Source", 96.0f, fontFamily, new Point(960, 100), textFormat, Brushes.White, outlinePen);

                        // Get the tally state of this source (we poll it),
                        NDI.NDIlib_tally_t NDI_tally = new NDI.NDIlib_tally_t();
                        NDI.Send.NDIlib_send_get_tally(sendInstancePtr, ref NDI_tally, 0);

                        // Do something different depending on where we are shown
                        if (NDI_tally.on_program)
                            DrawPrettyText(graphics, "On Program", 96.0f, fontFamily, new Point(960, 225), textFormat, Brushes.White, outlinePen);
                        else if (NDI_tally.on_preview)
                            DrawPrettyText(graphics, "On Preview", 96.0f, fontFamily, new Point(960, 225), textFormat, Brushes.White, outlinePen);

                        //// show what frame we've rendered
                        DrawPrettyText(graphics, String.Format("Frame {0}", frameNumber.ToString()), 96.0f, fontFamily, new Point(960, 350), textFormat, Brushes.White, outlinePen);

                        // show current time
                        DrawPrettyText(graphics, System.DateTime.Now.ToString(), 96.0f, fontFamily, new Point(960, 900), textFormat, Brushes.White, outlinePen);
                        */

                        try
                        {
                            // We now submit the frame. Note that this call will be clocked so that we end up submitting 
                            // at exactly 29.97fps.
                            NDIlib.send_send_video_v2(sendInstancePtr, ref videoFrame);

                            // Just display something helpful in the console
                            FLogger.Log(LogType.Debug, "Frame number " + frameNumber + " sent.");
                            //Console.WriteLine("Frame number {0} sent.", frameNumber);
                        }
                        catch (Exception e)
                        {
                            FLogger.Log(LogType.Error, "Send: " + e.Message);
                        }
                    }
                }
            }
        }
    }
}
