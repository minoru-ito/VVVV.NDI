using System;
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
            ISpread<string> FInSourceName;

            [Input("Framerate", MinValue = 1, DefaultValue = 30)]
            ISpread<uint> FInFramerate;

            [Input("Clock Video", DefaultBoolean = true)]
            ISpread<bool> FInClockVideo;

            [Input("Clock Audio", DefaultBoolean = false)]
            ISpread<bool> FInClockAudio;

            [Input("Connect")]
            IDiffSpread<bool> FInConnect;

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
            SlimDX.DXGI.Format format = SlimDX.DXGI.Format.Unknown;
            uint width = 0;
            uint height = 0;
            uint fps = 0;
            //bool textureValid = false;
            long dataStreamLength;
            byte[] srcBuffer;
            byte[] convertBuffer;

            IntPtr sendInstancePtr = IntPtr.Zero;
            IntPtr bufferPtr;
            NDIlib.video_frame_v2_t videoFrame;

            bool initialized = false;
            bool instanceCreated = false;
            bool videoFrameDefined = false;
            bool disposed = false;

            // use texture array for avoid readback delay
            uint dstSize = 2;
            uint dstIndex;
            uint readBackIndex;
            bool dstReady = false;
            bool readbackReady = false;
            Texture2D[] dst;// = DX11Texture2D.CreateStaging(AssignedContext, src);



            public void OnImportsSatisfied()
            {
                FOutVersion[0] = Marshal.PtrToStringAnsi(NDIlib.version());

                // Not required, but "correct". (see the SDK documentation)
                if (!NDIlib.initialize())
                {
                    // Cannot run NDI. Most likely because the CPU is not sufficient (see SDK documentation).
                    // you can check this directly with a call to NDIlib_is_supported_CPU()
                    if(!NDIlib.is_supported_CPU())
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

                    //_findInstance = new Finder(true);
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
                        foreach(Texture2D tex in dst)
                        {
                            tex.Dispose();
                        }

                        srcBuffer = null;
                        convertBuffer = null;

                        // free our buffer
                        Marshal.FreeHGlobal(bufferPtr);

                        // Destroy the NDI sender
                        if (sendInstancePtr != IntPtr.Zero)
                            NDIlib.send_destroy(sendInstancePtr);

                        // Not required, but "correct". (see the SDK documentation)
                        NDIlib.destroy();

                        disposed = true;
                    }
                }
            }

            public void Evaluate(int SpreadMax)
            {
                if (!initialized)
                    return;

                if(FInConnect.IsChanged)
                {
                    if(FInConnect[0])
                    {
                        if (!string.IsNullOrEmpty(FInSourceName[0]))
                            CreateSendInstance();
                    }
                    else
                    {
                        instanceCreated = false;
                    }
                }

                if (!instanceCreated)
                    return;

                if(FInTexture.PluginIO.IsConnected)
                {
                    if(RenderRequest != null) { RenderRequest(this, FHost); }

                    if(AssignedContext == null) { return; }
                    //Do NOT cache this, assignment done by the host
                    
                    if(FInTexture[0].Contains(AssignedContext))
                    {
                        functionA();
                    }
                    else
                    {
                        //
                    }
                }
            }

            void functionA()
            {
                Texture2D src = FInTexture[0][AssignedContext].Resource;

                if (!videoFrameDefined || format != src.Description.Format || width != src.Description.Width || height != src.Description.Height || fps != FInFramerate[0])
                    DefineVideoFrame(AssignedContext, src);

                //FLogger.Log(LogType.Debug, "DefineVideoFrame() called");

                if (!videoFrameDefined)
                    return;

                //FLogger.Log(LogType.Debug, "videoFrameDefined");

                try
                {
                    //FLogger.Log(LogType.Debug, "[A] " + readbackReady + "," + dstIndex + "," + readBackIndex);

                    if (readbackReady)
                    {
                        // create Texture for get resource from GPU to CPU
                        //Texture2D dst = DX11Texture2D.CreateStaging(AssignedContext, src);
                        //if(dst == null)
                        //    dst = DX11Texture2D.CreateStaging(AssignedContext, src);

                        // copy resource
                        AssignedContext.CurrentDeviceContext.CopyResource(src, dst[dstIndex]);

                        // get dataBox to access byte buffer
                        // MapFlags.DoNotWait will throw exception when GPU not ready
                        DataBox db = AssignedContext.CurrentDeviceContext.MapSubresource(dst[readBackIndex], 0, MapMode.Read, MapFlags.None); //.DoNotWait);// .None);

                        if (db == null)
                            FLogger.Log(LogType.Debug, "db == null");
                        else
                            FLogger.Log(LogType.Debug, width + "," + height + "," + db.Data.Length);

                        // create buffer
                        if (dataStreamLength != db.Data.Length)
                        {
                            dataStreamLength = db.Data.Length;
                            srcBuffer = new byte[dataStreamLength];
                            convertBuffer = new byte[dataStreamLength];

                            FLogger.Log(LogType.Debug, "dataStreamLength: " + dataStreamLength);
                        }

                        // test 1: read byte buffer and copy to IntPtr
                        db.Data.Read(srcBuffer, 0, (int)dataStreamLength);
                        // test 2: use CopyMemory method
                        //CopyMemory(bufferPtr, db.Data.DataPointer, (int)dataStreamLength);

                        // convert color format
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

                        if (FInSend[0])
                        {
                            Send();
                        }

                        // unmap resource
                        AssignedContext.CurrentDeviceContext.UnmapSubresource(dst[readBackIndex], 0);

                        db = null;

                        //dst.Dispose(); // this will slow down but needed
                        //dst = null;
                    }

                    // update index to shift target texture
                    if (++dstIndex >= dstSize)
                    {
                        // set flag after texture array filled
                        if (!readbackReady)
                            readbackReady = true;

                        dstIndex = 0;
                    }

                    if (++readBackIndex >= dstSize)
                        readBackIndex = 0;

                    //textureValid = true;

                    // set flag to false
                    //isReady = false;
                }
                catch (Exception e)
                {
                    FLogger.Log(LogType.Error, e.Message);

                    //textureValid = false;
                    return;
                }
            }

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

            void Send()
            {
                // are we connected to anyone?
                //if (NDI.Send.NDIlib_send_get_no_connections(sendInstancePtr, 10000) < 1)
                if (NDIlib.send_get_no_connections(sendInstancePtr, FInTimeout[0]) < 1)
                {
                    // no point rendering
                    FLogger.Log(LogType.Debug, "No current connections, so no rendering needed.");
                    //Console.WriteLine("No current connections, so no rendering needed.");

                    // Wait a bit, otherwise our limited example will end before you can connect to it
                    //System.Threading.Thread.Sleep(50);
                }
                else
                {
                    // We now submit the frame. Note that this call will be clocked so that we end up submitting 
                    // at exactly 29.97fps.
                    //NDI.Send.NDIlib_send_send_video(sendInstancePtr, ref videoFrame);
                    NDIlib.send_send_video_async_v2(sendInstancePtr, ref videoFrame);
                }
            }

            void CreateSendInstance()
            {
                if (!initialized)
                    return;

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
                    clock_video = FInClockVideo[0], //true,
                    clock_audio = FInClockAudio[0] //false
                };

                // destroy if exists
                if (sendInstancePtr != IntPtr.Zero)
                    NDIlib.send_destroy(sendInstancePtr);

                // We create the NDI finder instance
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
            }

            void DefineVideoFrame(DX11RenderContext assignedContext, Texture2D texture)
            {
                //
            }
        }
    }
}