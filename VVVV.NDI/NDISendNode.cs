using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.ComponentModel.Composition;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
using VVVV.Core.Logging;
using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using SlimDX;
using SlimDX.Direct3D11;
using FeralTic.DX11;
using FeralTic.DX11.Resources;
using NewTek;
using NewTek.NDI;

namespace VVVV.DX11.Nodes
{
    namespace VVVV.NDI
    {
        [PluginInfo(Name = "Send", Version = "DX11", Category = "NDI", AutoEvaluate = true)]
        public class NDISendNode : IPluginEvaluate, IPartImportsSatisfiedNotification, IDisposable, IDX11ResourceDataRetriever
        {
            [Input("Texture In")]
            Pin<DX11Resource<DX11Texture2D>> FInTexture;

            [Input("Source Name", DefaultString = "Example")]
            IDiffSpread<string> FInSourceName;

            [Input("Framerate", MinValue = 1, DefaultValue = 30)]
            ISpread<int> FInFramerate;

            //[Input("Clock Video", DefaultBoolean = true)]
            //ISpread<bool> FInClockVideo;

            //[Input("Clock Audio", DefaultBoolean = false)]
            //ISpread<bool> FInClockAudio;

            //[Input("Connect")]
            //IDiffSpread<bool> FInConnect;

            [Input("Send", IsBang = true)]
            ISpread<bool> FInSend;

            [Input("Timeout", MinValue = 0, DefaultValue = 1000)]
            ISpread<uint> FInTimeout;

            [Input("RGBA to BGRA")]
            ISpread<bool> FInRGBAtoBGRA;


            [Output("Version", Visibility = PinVisibility.Hidden)]
            ISpread<string> FOutVersion;

            [Output("Initialized", Visibility = PinVisibility.Hidden)]
            ISpread<bool> FOutInitialized;

            [Import()]
            public ILogger FLogger;

            [Import()]
            IPluginHost FHost;

            public DX11RenderContext AssignedContext { get; set; }
            public event DX11RenderRequestDelegate RenderRequest;
            //SlimDX.DXGI.Format format = SlimDX.DXGI.Format.Unknown;
            //uint width = 0;
            //uint height = 0;
            //uint fps = 0;
            ////bool textureValid = false;
            long dataStreamLength;
            byte[] srcBuffer;
            byte[] convertBuffer;

            //IntPtr sendInstancePtr = IntPtr.Zero;
            //IntPtr bufferPtr;
            //NDIlib.video_frame_v2_t videoFrame;

            bool initialized = false;
            bool instanceCreated = false;
            //bool videoFrameDefined = false;
            bool disposed = false;

            // use texture array for avoid readback delay
            //uint dstSize = 2;
            //uint dstIndex;
            //uint readBackIndex;
            //bool dstReady = false;
            //bool readbackReady = false;
            //Texture2D[] dst;// = DX11Texture2D.CreateStaging(AssignedContext, src);


            // ===

            private Object sendInstanceLock = new Object();
            private IntPtr sendInstancePtr = IntPtr.Zero;

            ////RenderTargetBitmap targetBitmap = null;

            private int stride;
            private int bufferSize;
            private float aspectRatio;

            // a thread to send frames on so that the UI isn't dragged down
            Thread sendThread = null;

            // a way to exit the thread safely
            bool exitThread = false;

            // a thread safe collection to store pending frames
            BlockingCollection<NDIlib.video_frame_v2_t> pendingFrames = new BlockingCollection<NDIlib.video_frame_v2_t>();

            // used for pausing the send thread
            bool isPausedValue = false;

            List<long> FrameDurations = new List<long>();
            private long PreviousFrameTime = 0;

            // ===


            public void OnImportsSatisfied()
            {
                FOutVersion[0] = Marshal.PtrToStringAnsi(NDIlib.version());

                // Not required, but "correct". (see the SDK documentation)
                if (!NDIlib.initialize())
                {
                    // Cannot run NDI. Most likely because the CPU is not sufficient (see SDK documentation).
                    // you can check this directly with a call to NDIlib_is_supported_CPU()
                    if (!NDIlib.is_supported_CPU())
                    {
                        FLogger.Log(LogType.Error, "Cannot run NDI. CPU unsupported.");
                    }
                    else
                    {
                        FLogger.Log(LogType.Error, "Cannot run NDI.");
                    }

                    FOutInitialized[0] = false;
                }
                else
                {
                    //FLogger.Log(LogType.Message, "is_supported_CPU: " + NDIlib.is_supported_CPU());
                    //FLogger.Log(LogType.Message, Marshal.PtrToStringAnsi(NDIlib.version()));

                    FOutInitialized[0] = true;
                    initialized = true;

                    // create send instance
                    if (FInSourceName.SliceCount > 0 && !string.IsNullOrEmpty(FInSourceName[0]))
                        CreateSendInstance();

                    // start up a thread to send
                    sendThread = new Thread(SendThreadProc) { IsBackground = true, Name = "NDISendThread" };
                    sendThread.Start();
                }
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
                        // tell the thread to exit
                        exitThread = true;

                        // wait for it to exit
                        if (sendThread != null)
                        {
                            sendThread.Join();
                            sendThread = null;
                        }

                        // cause the pulling of frames to fail
                        pendingFrames.CompleteAdding();

                        // clear any pending frames
                        while (pendingFrames.Count > 0)
                        {
                            NDIlib.video_frame_v2_t discardFrame = pendingFrames.Take();
                            Marshal.FreeHGlobal(discardFrame.p_data);
                        }

                        pendingFrames.Dispose();
                        
                        // Destroy the NDI sender
                        if (sendInstancePtr != IntPtr.Zero)
                        {
                            NDIlib.send_destroy(sendInstancePtr);
                            sendInstancePtr = IntPtr.Zero;
                        }

                        // Not required, but "correct". (see the SDK documentation)
                        NDIlib.destroy();

                        //

                        //foreach(Texture2D tex in dst)
                        //{
                        //    tex.Dispose();
                        //}

                        srcBuffer = null;
                        convertBuffer = null;

                        // free our buffer
                        //Marshal.FreeHGlobal(bufferPtr);

                        //

                        disposed = true;
                    }
                }
            }

            public void Evaluate(int SpreadMax)
            {
                if (!initialized)
                    return;

                // (re-)create send instance when name changed 
                if (FInSourceName.IsChanged)
                {
                    if (FInSourceName.SliceCount > 0 && !string.IsNullOrEmpty(FInSourceName[0]))
                    {
                        CreateSendInstance();
                    }
                    else
                    {
                        instanceCreated = false;
                    }
                }

                if (!instanceCreated)
                    return;

                if (FInTexture.PluginIO.IsConnected)
                {
                    if (RenderRequest != null) { RenderRequest(this, FHost); }

                    if (AssignedContext == null) { return; }
                    //Do NOT cache this, assignment done by the host

                    if (FInTexture[0].Contains(AssignedContext))
                    {
                        updateSendBuffer();
                    }
                    else
                    {
                        //
                    }
                }
            }

            void updateSendBuffer()
            {
                FrameDurations.Add(DateTime.Now.Ticks - PreviousFrameTime);
                PreviousFrameTime = DateTime.Now.Ticks;

                Texture2D src = FInTexture[0][AssignedContext].Resource;

                int xres = src.Description.Width;
                int yres = src.Description.Height;

                //FLogger.Log(LogType.Message, xres + "," + yres);

                //int frNum = 0;// NdiFrameRateNumerator;
                //int frDen = 0;// NdiFrameRateDenominator;

                // sanity
                if (sendInstancePtr == IntPtr.Zero || xres < 8 || yres < 8)
                    return;

                stride = (xres * 32/*BGRA bpp*/ + 7) / 8;
                bufferSize = yres * stride;
                aspectRatio = (float)xres / (float)yres;
                
                //if(bufferPtr != IntPtr.Zero)
                //{
                //    // free our buffer
                //    Marshal.FreeHGlobal(bufferPtr);
                //}

                // allocate some memory for a video buffer
                //bufferPtr = Marshal.AllocHGlobal(bufferSize);
                IntPtr bufferPtr = Marshal.AllocHGlobal(bufferSize);

                // We are going to create a progressive frame at 60Hz.
                NDIlib.video_frame_v2_t videoFrame = new NDIlib.video_frame_v2_t()
                {
                    // Resolution
                    xres = src.Description.Width,
                    yres = src.Description.Height,
                    // Use BGRA video
                    FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
                    // The frame-eate
                    frame_rate_N = FInFramerate[0] * 1000,
                    frame_rate_D = 1000,
                    // The aspect ratio
                    picture_aspect_ratio = aspectRatio,
                    // This is a progressive frame
                    frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                    // Timecode.
                    timecode = 0,
                    // The video memory used for this frame
                    p_data = bufferPtr,
                    // The line to line stride of this image
                    line_stride_in_bytes = stride,
                    // no metadata
                    p_metadata = IntPtr.Zero,
                    // only valid on received frames
                    timestamp = NDIlib.recv_timestamp_undefined
                };
                
                // copy data to buffer
                TextureToBuffer(src, ref bufferPtr);

                // add it to the output queue
                if (!AddFrame(videoFrame))
                    FLogger.Log(LogType.Error, "failed to add video frame");
            }

            void TextureToBuffer(Texture2D src, ref IntPtr buffer)
            {
                try
                {
                    // create texture for get resource from GPU to CPU
                    Texture2D dst = DX11Texture2D.CreateStaging(AssignedContext, src);

                    // copy resource from src
                    AssignedContext.CurrentDeviceContext.CopyResource(src, dst);

                    // get databox to access byte buffer
                    // MapFlags.DoNotWait will throw exception when GPU not ready
                    DataBox db = AssignedContext.CurrentDeviceContext.MapSubresource(dst, 0, MapMode.Read, MapFlags.None);

                    // create buffer
                    if(dataStreamLength != db.Data.Length)
                    {
                        dataStreamLength = db.Data.Length;
                        srcBuffer = new byte[dataStreamLength];
                    }

                    // read data
                    db.Data.Read(srcBuffer, 0, (int)dataStreamLength);

                    // convert color if need
                    // ...
                    /*
                    if (FInRGBAtoBGRA[0])
                    {
                        ConvertBuffer();
                            
                        Marshal.Copy(convertBuffer, 0, bufferPtr, (int)dataStreamLength);
                    }
                    else
                    {
                        //await db.Data.ReadAsync(srcBuffer, 0, (int)dataStreamLength);
                        Marshal.Copy(srcBuffer, 0, bufferPtr, (int)dataStreamLength);
                    }
                     */

                    // byte array to IntPtr
                    Marshal.Copy(srcBuffer, 0, buffer, (int)dataStreamLength);

                    // unmap resource
                    AssignedContext.CurrentDeviceContext.UnmapSubresource(dst, 0);

                    db = null;

                    // this will slow down but needed
                    dst.Dispose();
                    dst = null;
                }
                catch(Exception e)
                {
                    FLogger.Log(LogType.Error, e.Message);

                    return;
                }
            }

            //void functionA()
            //{
            //    Texture2D src = FInTexture[0][AssignedContext].Resource;

            //    if (!videoFrameDefined || format != src.Description.Format || width != src.Description.Width || height != src.Description.Height || fps != FInFramerate[0])
            //        DefineVideoFrame(AssignedContext, src);

            //    //FLogger.Log(LogType.Debug, "DefineVideoFrame() called");

            //    if (!videoFrameDefined)
            //        return;

            //    //FLogger.Log(LogType.Debug, "videoFrameDefined");

            //    try
            //    {
            //        //FLogger.Log(LogType.Debug, "[A] " + readbackReady + "," + dstIndex + "," + readBackIndex);

            //        if (readbackReady)
            //        {
            //            // create Texture for get resource from GPU to CPU
            //            //Texture2D dst = DX11Texture2D.CreateStaging(AssignedContext, src);
            //            //if(dst == null)
            //            //    dst = DX11Texture2D.CreateStaging(AssignedContext, src);

            //            // copy resource
            //            AssignedContext.CurrentDeviceContext.CopyResource(src, dst[dstIndex]);

            //            // get dataBox to access byte buffer
            //            // MapFlags.DoNotWait will throw exception when GPU not ready
            //            DataBox db = AssignedContext.CurrentDeviceContext.MapSubresource(dst[readBackIndex], 0, MapMode.Read, MapFlags.None); //.DoNotWait);// .None);

            //            if (db == null)
            //                FLogger.Log(LogType.Debug, "db == null");
            //            else
            //                FLogger.Log(LogType.Debug, width + "," + height + "," + db.Data.Length);

            //            // create buffer
            //            if (dataStreamLength != db.Data.Length)
            //            {
            //                dataStreamLength = db.Data.Length;
            //                srcBuffer = new byte[dataStreamLength];
            //                convertBuffer = new byte[dataStreamLength];

            //                FLogger.Log(LogType.Debug, "dataStreamLength: " + dataStreamLength);
            //            }

            //            // test 1: read byte buffer and copy to IntPtr
            //            db.Data.Read(srcBuffer, 0, (int)dataStreamLength);
            //            // test 2: use CopyMemory method
            //            //CopyMemory(bufferPtr, db.Data.DataPointer, (int)dataStreamLength);

            //            // convert color format
            //            if (FInRGBAtoBGRA[0])
            //            {
            //                ConvertBuffer();
                            
            //                Marshal.Copy(convertBuffer, 0, bufferPtr, (int)dataStreamLength);
            //            }
            //            else
            //            {
            //                //await db.Data.ReadAsync(srcBuffer, 0, (int)dataStreamLength);
            //                Marshal.Copy(srcBuffer, 0, bufferPtr, (int)dataStreamLength);
            //            }

            //            if (FInSend[0])
            //            {
            //                Send();
            //            }

            //            // unmap resource
            //            AssignedContext.CurrentDeviceContext.UnmapSubresource(dst[readBackIndex], 0);

            //            db = null;

            //            //dst.Dispose(); // this will slow down but needed
            //            //dst = null;
            //        }

            //        // update index to shift target texture
            //        if (++dstIndex >= dstSize)
            //        {
            //            // set flag after texture array filled
            //            if (!readbackReady)
            //                readbackReady = true;

            //            dstIndex = 0;
            //        }

            //        if (++readBackIndex >= dstSize)
            //            readBackIndex = 0;

            //        //textureValid = true;

            //        // set flag to false
            //        //isReady = false;
            //    }
            //    catch (Exception e)
            //    {
            //        FLogger.Log(LogType.Error, e.Message);

            //        //textureValid = false;
            //        return;
            //    }
            //}

            void ConvertBuffer()
            {
                /*
                unsafe
                {
                    fixed(byte* bp = srcBuffer)
                    {
                        uint* ip = (uint*)bp;
                        int end = (int)dataStreamLength / 4;
                        for (int i = 0; i < end; i++)
                        {
                            ip[i] = (ip[i] & 0x000000ff) << 16 | (ip[i] & 0x0000FF00) | (ip[i] & 0x00FF0000) >> 16 | (ip[i] & 0xFF000000);
                        }
                    }

                    Marshal.Copy(srcBuffer, 0, bufferPtr, (int)dataStreamLength);
                }
                */
                for (int i = 0; i < dataStreamLength; i += 4)
                {
                    convertBuffer[i] = srcBuffer[i + 2];      // B
                    convertBuffer[i + 1] = srcBuffer[i + 1];  // G
                    convertBuffer[i + 2] = srcBuffer[i];      // R
                    convertBuffer[i + 3] = srcBuffer[i + 3];  // A
                }
            }

            //void Send()
            //{
            //    // are we connected to anyone?
            //    //if (NDI.Send.NDIlib_send_get_no_connections(sendInstancePtr, 10000) < 1)
            //    if (NDIlib.send_get_no_connections(sendInstancePtr, FInTimeout[0]) < 1)
            //    {
            //        // no point rendering
            //        FLogger.Log(LogType.Debug, "No current connections, so no rendering needed.");
            //        //Console.WriteLine("No current connections, so no rendering needed.");

            //        // Wait a bit, otherwise our limited example will end before you can connect to it
            //        //System.Threading.Thread.Sleep(50);
            //    }
            //    else
            //    {
            //        // We now submit the frame. Note that this call will be clocked so that we end up submitting 
            //        // at exactly 29.97fps.
            //        //NDI.Send.NDIlib_send_send_video(sendInstancePtr, ref videoFrame);
            //        NDIlib.send_send_video_async_v2(sendInstancePtr, ref videoFrame);
            //    }
            //}

            // prepare to send texture
            void CreateSendInstance()
            {
                if (!initialized)
                    return;

                Monitor.Enter(sendInstanceLock);
                {
                    // reset flag
                    instanceCreated = false;

                    // .Net interop doesn't handle UTF-8 strings, so do it manually
                    // These must be freed later
                    IntPtr sourceNamePtr = UTF.StringToUtf8(FInSourceName[0]);
                    IntPtr groupsNamePtr = IntPtr.Zero;

                    // Create an NDI source description using sourceNamePtr and it's clocked to the video.
                    NDIlib.send_create_t createDesc = new NDIlib.send_create_t()
                    {
                        p_ndi_name = sourceNamePtr,
                        p_groups = groupsNamePtr,
                        clock_video = true,
                        clock_audio = false
                    };

                    // destroy if exists
                    if (sendInstancePtr != IntPtr.Zero)
                    {
                        NDIlib.send_destroy(sendInstancePtr);
                        sendInstancePtr = IntPtr.Zero;
                    }

                    // We create the NDI sender instance
                    sendInstancePtr = NDIlib.send_create(ref createDesc);

                    // free the strings we allocated
                    Marshal.FreeHGlobal(sourceNamePtr);
                    Marshal.FreeHGlobal(groupsNamePtr);

                    // did it succeed?
                    if (sendInstancePtr == IntPtr.Zero)
                    {
                        FLogger.Log(LogType.Error, "Failed to create send instance");
                        return;
                    }
                    else
                    {
                        FLogger.Log(LogType.Message, "Successed to create send instance");

                        instanceCreated = true;
                    }

                    // unlock
                    Monitor.Exit(sendInstanceLock);
                }
            }

            //void DefineVideoFrame(DX11RenderContext assignedContext, Texture2D texture)
            //{
            //    //FLogger.Log(LogType.Message, "DefineVideoFrame()");

            //    try
            //    {
            //        // reset flag
            //        videoFrameDefined = false;

            //        // update format, width, height
            //        format = texture.Description.Format;
            //        width = (uint)texture.Description.Width;
            //        height = (uint)texture.Description.Height;
            //        fps = FInFramerate[0];

            //        //FOutFormat[0] = format.ToString();

            //        //if (FInLog[0])
            //        //{
            //        FLogger.Log(LogType.Debug, "[DefineVideoFrame] format: " + format + ", width: " + width + ", height: " + height + ", fps: " + fps);
            //        //}

            //        // define our bitmap properties
            //        //uint xres = 1920;
            //        //uint yres = 1080;
            //        //uint stride = (xres * 32/*BGRA bpp*/ + 7) / 8;
            //        //uint bufferSize = yres * stride;
            //        uint stride = (width * 32 + 7) / 8;
            //        uint bufferSize = height * stride;

            //        //if (FInLog[0])
            //        //{
            //        FLogger.Log(LogType.Debug, "[DefineVideoFrame] stride: " + stride + ", bufferSize: " + bufferSize);
            //        //}

            //        // mino
            //        if (bufferPtr != IntPtr.Zero)
            //        {
            //            // free our buffer
            //            Marshal.FreeHGlobal(bufferPtr);
            //        }

            //        // allocate some memory for a video buffer
            //        bufferPtr = Marshal.AllocHGlobal((int)bufferSize);

            //        // We are going to create a 1920x1080 progressive frame at 29.97Hz.
            //        videoFrame = new NDI.NDIlib_video_frame_t()
            //        {
            //            // Resolution
            //            xres = width,//xres,
            //            yres = height,// yres,
            //                          // Use BGRA video
            //            FourCC = NDI.NDIlib_FourCC_type_e.NDIlib_FourCC_type_BGRA,
            //            // The frame-eate
            //            frame_rate_N = fps * 1000, //30000,
            //            frame_rate_D = 1000, //1001,
            //                                 // The aspect ratio (16:9)
            //            picture_aspect_ratio = ((float)width / (float)height), //(16.0f / 9.0f),
            //                                                                   // This is a progressive frame
            //            frame_format_type = NDI.NDIlib_frame_format_type_e.NDIlib_frame_format_type_progressive,
            //            // Timecode.
            //            timecode = 0,
            //            // The video memory used for this frame
            //            p_data = bufferPtr,
            //            // The line to line stride of this image
            //            line_stride_in_bytes = (uint)stride
            //        };

            //        // reset if exists
            //        if (readbackReady)
            //        {
            //            // reset flag
            //            readbackReady = false;

            //            // clear texture
            //            if (dst != null)
            //            {
            //                foreach (Texture2D tex in dst)
            //                {
            //                    tex.Dispose();
            //                }
            //                dst = null;
            //            }
            //        }

            //        // prepare texture for readback
            //        dst = new Texture2D[dstSize];
            //        for (int i = 0; i < dstSize; i++)
            //        {
            //            dst[i] = DX11Texture2D.CreateStaging(assignedContext, texture);
            //        }

            //        // reset index
            //        dstIndex = 0;
            //        readBackIndex = dstSize - 1;

            //        videoFrameDefined = true;
            //    }
            //    catch (Exception e)
            //    {
            //        FLogger.Log(LogType.Error, "[DefineVideoFrame] " + e.Message);
            //    }
            //}


            //

            private void SendThreadProc()
            {
                // look for changes in tally
                bool lastProg = false;
                bool lastPrev = false;

                NDIlib.tally_t tally = new NDIlib.tally_t();
                tally.on_program = lastProg;
                tally.on_preview = lastPrev;

                while(!exitThread)
                {
                    if(Monitor.TryEnter(sendInstanceLock))
                    {
                        // if this is not here, then we must be being reconfigured
                        if(sendInstancePtr == null)
                        {
                            // unlock
                            Monitor.Exit(sendInstanceLock);

                            // give up some time
                            Thread.Sleep(20);

                            // loop again
                            continue;
                        }

                        try
                        {
                            // get the next available frame
                            NDIlib.video_frame_v2_t frame;
                            if(pendingFrames.TryTake(out frame, 250))
                            {
                                // this dropps frames if the UI is rendernig ahead of the specified NDI frame rate
                                while (pendingFrames.Count > 1)
                                {
                                    NDIlib.video_frame_v2_t discardFrame = pendingFrames.Take();
                                    Marshal.FreeHGlobal(discardFrame.p_data);
                                }

                                // We now submit the frame. Note that this call will be clocked so that we end up submitting 
                                // at exactly the requested rate.
                                // If WPF can't keep up with what you requested of NDI, then it will be sent at the rate WPF is rendering.
                                //if (!isPausedValue)
                                if(FInSend[0])
                                {
                                    NDIlib.send_send_video_v2(sendInstancePtr, ref frame);
                                }

                                // free the memory from this frame
                                Marshal.FreeHGlobal(frame.p_data);
                            }
                        }
                        catch(OperationCanceledException)
                        {
                            pendingFrames.CompleteAdding();
                        }
                        catch
                        {
                            //
                        }

                        // unlock
                        Monitor.Exit(sendInstanceLock);
                    }
                    else
                    {
                        Thread.Sleep(20);
                    }

                    // check tally
                    NDIlib.send_get_tally(sendInstancePtr, ref tally, 0);

                    // if tally changed trigger an update
                    if(lastProg != tally.on_program || lastPrev != tally.on_preview)
                    {
                        // save the last values
                        lastProg = tally.on_program;
                        lastPrev = tally.on_preview;
                    }
                }
            }

            private bool AddFrame(NDIlib.video_frame_v2_t frame)
            {
                try
                {
                    pendingFrames.Add(frame);
                }
                catch(OperationCanceledException)
                {
                    // we're shutting down
                    pendingFrames.CompleteAdding();
                    return false;
                }
                catch
                {
                    return false;
                }

                return true;
            }
        }
    }
}