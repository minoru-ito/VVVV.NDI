using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.ComponentModel.Composition;
using VVVV.Core.Logging;
using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using SlimDX.Direct3D11;
using FeralTic.DX11;
using FeralTic.DX11.Resources;
using NewTek;
using NewTek.NDI;

namespace VVVV.DX11.Nodes
{
    namespace VVVV.NDI
    {
        [PluginInfo(Name = "Receive", Version = "DX11", Category = "NDI")]
        public class NDIReceiveNode : IPluginEvaluate, IPartImportsSatisfiedNotification, IDisposable, IDX11ResourceHost
        {
            [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
            //[DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
            public static extern void CopyMemory(IntPtr dest, IntPtr src, int count);


            [Input("Source", IsSingle = true)]
            IDiffSpread<Source> FInSource;

            [Input("Receiver Name", DefaultString = "Receiver")]
            ISpread<string> FInReceiverName;

            //[Input("Connect")]
            //IDiffSpread<bool> FInConnect;

            //[Input("Update", IsBang = true)]
            //ISpread<bool> FInUpdate;


            [Output("Texture Out")]
            ISpread<DX11Resource<DX11DynamicTexture2D>> FOutTexture;

            [Output("Width")]
            ISpread<int> FOutWidth;

            [Output("Height")]
            ISpread<int> FOutHeight;

            [Output("Buffer Size")]
            ISpread<int> FOutBufferSize;

            //[Output("Key")]
            //ISpread<string> FOutKey;

            //[Output("Format")]
            //ISpread<string> FOutFormat;

            [Output("Version", Visibility = PinVisibility.Hidden)]
            ISpread<string> FOutVersion;
            
            [Output("Initialized", Visibility = PinVisibility.Hidden)]
            ISpread<bool> FOutInitialized;

            [Import()]
            public ILogger FLogger;

            //Byte[] buffer = null;
            IntPtr buffer_ptr = IntPtr.Zero;
            int width = 0;
            int height = 0;
            int bufferSize;
            //bool initialized = false;
            bool invalidate = false;        // do update texture when this flag = true
            bool lockBuffer = false;        // not update buffer when this flag = true
            bool disposed = false;          // instance of this class is disposed or not

            // a pointer to our unmanaged NDI finder instance
            //IntPtr _findInstancePtr = IntPtr.Zero;

            // a pointer to our unmanaged NDI receiver instance
            IntPtr _recvInstancePtr = IntPtr.Zero;

            // a thread to receive frames on so that the UI is still functional
            Thread _receiveThread = null;

            // a way to exit the thread safely
            bool _exitThread = false;

            //Finder _findInstance;

            // a map of names to sources
            //Dictionary<String, NDIlib.source_t> _sources = new Dictionary<string, NDIlib.source_t>();

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
                    FOutInitialized[0] = true;
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
                        _exitThread = true;

                        // wait for it to exit
                        if(_receiveThread != null)
                        {
                            _receiveThread.Join();

                            _receiveThread = null;
                        }

                        // destroy the NDI find instance
                        //if(_findInstancePtr != IntPtr.Zero)
                        //{
                        //    NDIlib.find_destroy(_findInstancePtr);
                        //    _findInstancePtr = IntPtr.Zero;
                        //}

                        // destroy the receiver
                        if(_recvInstancePtr != IntPtr.Zero)
                        {
                            NDIlib.recv_destroy(_recvInstancePtr);
                            _recvInstancePtr = IntPtr.Zero;
                        }

                        //if (_findInstance != null)
                        //    _findInstance.Dispose();

                        // Not required, but "correct". (see the SDK documentation)
                        NDIlib.destroy();

                        if(FOutTexture.SliceCount > 0)
                        {
                            if (FOutTexture[0] != null)
                                FOutTexture[0].Dispose();
                        }

                        disposed = true;
                    }
                }
            }

            public void Evaluate(int SpreadMax)
            {
                if(FInSource.IsChanged)
                {
                    if (FInSource.SliceCount == 0 || FInSource[0] == null)
                    {
                        Disconnect();
                    }
                    else
                    {
                        Connect(FInSource[0]);
                    }
                }

                if(_recvInstancePtr == IntPtr.Zero)
                {
                    if(FOutTexture.SliceCount == 1)
                    {
                        if(FOutTexture[0] != null)
                        {
                            FOutTexture[0].Dispose();
                        }
                        FOutTexture.SliceCount = 0;

                        return;
                    }
                }
                else
                {
                    FOutTexture.SliceCount = 1;

                    if (FOutTexture[0] == null)
                    {
                        //FLogger.Log(LogType.Debug, "invalidate = true");
                        FOutTexture[0] = new DX11Resource<DX11DynamicTexture2D>();
                    }

                    invalidate = true;
                }

                if (FInSource.SliceCount == 0 || FInSource[0] == null)
                {
                    FOutWidth[0] = 0;
                    FOutHeight[0] = 0;
                    FOutBufferSize[0] = 0;
                }
                else
                {
                    FOutWidth[0] = width;
                    FOutHeight[0] = height;
                    FOutBufferSize[0] = bufferSize;
                }
            }

            unsafe public void Update(DX11RenderContext context)
            {
                if (FOutTexture.SliceCount == 0 || buffer_ptr == IntPtr.Zero) { return; }

                if (invalidate || !FOutTexture[0].Contains(context))
                {
                    // do not update buffer from here
                    lockBuffer = true;

                    SlimDX.DXGI.Format fmt;
                    Texture2DDescription desc;

                    fmt = SlimDX.DXGI.Format.B8G8R8A8_UNorm;
                    if (FOutTexture[0].Contains(context))
                    {
                        desc = FOutTexture[0][context].Resource.Description;

                        if (desc.Width != width || desc.Height != height || desc.Format != fmt)
                        {
                            FOutTexture[0].Dispose(context);
                            FOutTexture[0][context] = new DX11DynamicTexture2D(context, width, height, fmt);
                        }
                    }
                    else
                    {
                        FOutTexture[0][context] = new DX11DynamicTexture2D(context, width, height, fmt);
                    }

                    desc = FOutTexture[0][context].Resource.Description;

                    // sometimes occur errors.
                    var t = FOutTexture[0][context];
                    t.WriteData(buffer_ptr, bufferSize);

                    invalidate = false;

                    // ready to update buffer
                    lockBuffer = false;
                }
            }

            public void Destroy(DX11RenderContext context, bool force)
            {
                FOutTexture[0].Dispose(context);
            }



            #region NDIReceive

            // connect to an NDI source in our Dictionary by name
            void Connect(Source source)
            {
                if (FInReceiverName.SliceCount == 0 || String.IsNullOrEmpty(FInReceiverName[0]))
                    FLogger.Log(LogType.Error, "receiverName can not be null or empty.");

                // just in case we're already connected
                Disconnect();

                // Sanity
                if (source == null || String.IsNullOrEmpty(source.Name))
                    return;

                // a source_t to describe the source to connect to.
                NDIlib.source_t source_t = new NDIlib.source_t()
                {
                    p_ndi_name = UTF.StringToUtf8(source.Name)
                };

                // make a description of the receiver we want
                NDIlib.recv_create_v3_t recvDescription = new NDIlib.recv_create_v3_t()
                {
                    // the source we selected
                    source_to_connect_to = source_t,

                    // we want BGRA frames for this example
                    color_format = NDIlib.recv_color_format_e.recv_color_format_BGRX_BGRA,

                    // we want full quality - for small previews or limited bandwidth, choose lowest
                    bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_highest,

                    // let NDIlib deinterlace for us if needed
                    allow_video_fields = false,

                    // The name of the NDI receiver to create. This is a NULL terminated UTF8 string and should be
                    // the name of receive channel that you have. This is in many ways symettric with the name of
                    // senders, so this might be "Channel 1" on your system.
                    p_ndi_recv_name = UTF.StringToUtf8(FInReceiverName[0])
                };

                // create a new instance connected to this source
                _recvInstancePtr = NDIlib.recv_create_v3(ref recvDescription);

                // free the memory we allocated with StringToUtf8
                Marshal.FreeHGlobal(source_t.p_ndi_name);
                Marshal.FreeHGlobal(recvDescription.p_ndi_recv_name);

                // did it work?
                //System.Diagnostics.Debug.Assert(_recvInstancePtr != IntPtr.Zero, "Failed to create NDI receive instance.");
                if (_recvInstancePtr == IntPtr.Zero)
                    FLogger.Log(LogType.Warning, "Failed to create NDI receive instance.");

                if (_recvInstancePtr != IntPtr.Zero)
                {
                    // We are now going to mark this source as being on program output for tally purposes (but not on preview)
                    SetTallyIndicators(true, false);

                    // start up a thread to receive on
                    _receiveThread = new Thread(ReceiveThreadProc) { IsBackground = true, Name = "NDIReceiveThread" };
                    _receiveThread.Start();
                }
            }

            void Disconnect()
            {
                // in case we're connected, reset the tally indicators
                SetTallyIndicators(false, false);

                // check for a running thread
                if (_receiveThread != null)
                {
                    // tell it to exit
                    _exitThread = true;

                    // wait for it to end
                    _receiveThread.Join();
                }

                // reset thread defaults
                _receiveThread = null;
                _exitThread = false;

                // Destroy the receiver
                NDIlib.recv_destroy(_recvInstancePtr);

                // set it to a safe value
                _recvInstancePtr = IntPtr.Zero;
            }

            void SetTallyIndicators(bool onProgram, bool onPreview)
            {
                // we need to have a receive instance
                if (_recvInstancePtr != IntPtr.Zero)
                {
                    // set up a state descriptor
                    NDIlib.tally_t tallyState = new NDIlib.tally_t()
                    {
                        on_program = onProgram,
                        on_preview = onPreview
                    };

                    // set it on the receiver instance
                    NDIlib.recv_set_tally(_recvInstancePtr, ref tallyState);
                }
            }

            void ReceiveThreadProc()
            {
                while (!_exitThread && _recvInstancePtr != IntPtr.Zero)
                {
                    // The descriptors
                    NDIlib.video_frame_v2_t videoFrame = new NDIlib.video_frame_v2_t();
                    NDIlib.audio_frame_v2_t audioFrame = new NDIlib.audio_frame_v2_t();
                    NDIlib.metadata_frame_t metadataFrame = new NDIlib.metadata_frame_t();

                    switch (NDIlib.recv_capture_v2(_recvInstancePtr, ref videoFrame, ref audioFrame, ref metadataFrame, 1000))
                    {
                        // No data
                        case NDIlib.frame_type_e.frame_type_none:
                            // No data received
                            break;
                        
                        // Video data
                        case NDIlib.frame_type_e.frame_type_video:

                            //FLogger.Log(LogType.Debug, "received");

                            // if not enabled or lockBuffer flag = true, just discard
                            // this can also occasionally happen when changing sources
                            if (videoFrame.p_data == IntPtr.Zero || lockBuffer)
                            {
                                // alreays free received frames
                                NDIlib.recv_free_video_v2(_recvInstancePtr, ref videoFrame);

                                break;
                            }
                            
                            // get all our info so that we can free the frame
                            int yres = (int)videoFrame.yres;
                            int xres = (int)videoFrame.xres;
                            
                            width = xres;
                            height = yres;

                            // quick and dirty aspect ratio correction for non-square pixels - SD 4:3, 16:9, etc.
                            double dpiX = 96.0 * (videoFrame.picture_aspect_ratio / ((double)xres / (double)yres));

                            int stride = (int)videoFrame.line_stride_in_bytes;
                            int size = yres * stride;

                            // allocate some memory for a video buffer
                            if (bufferSize != size)
                            {
                                if (buffer_ptr != IntPtr.Zero)
                                    Marshal.FreeHGlobal(buffer_ptr);

                                buffer_ptr = Marshal.AllocHGlobal((int)size);

                                bufferSize = size;
                            }
                            
                            // copy frame data
                            CopyMemory(buffer_ptr, videoFrame.p_data, bufferSize);

                            // free frames that were received
                            NDIlib.recv_free_video_v2(_recvInstancePtr, ref videoFrame);

                            // set flag for update texture
                            //invalidate = true;

                            break;

                        // not support audio
                        case NDIlib.frame_type_e.frame_type_audio:
                            // always free received frames
                            NDIlib.recv_free_audio_v2(_recvInstancePtr, ref audioFrame);

                            break;
                        
                        // Metadata
                        case NDIlib.frame_type_e.frame_type_metadata:

                            // UTF-8 strings must be converted for use - length includes the terminating zero
                            //String metadata = Utf8ToString(metadataFrame.p_data, metadataFrame.length-1);

                            //System.Diagnostics.Debug.Print(metadata);

                            // free frames that were received
                            NDIlib.recv_free_metadata(_recvInstancePtr, ref metadataFrame);
                            break;
                    }
                }
            }

            #endregion
        }
    }
}