using System;
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
using SlimDX.Direct3D11;
using FeralTic.DX11;
using FeralTic.DX11.Resources;
using NewTek;

namespace VVVV.DX11.Nodes
{
    namespace VVVV.NDI
    {
        [PluginInfo(Name = "Receive", Version = "DX11", Category = "NDI")]
        public class NDIReceiveNode : IPluginEvaluate, IPartImportsSatisfiedNotification, IDisposable, IDX11ResourceHost
        {
            [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
            public static extern void CopyMemory(IntPtr dest, IntPtr src, int count);


            [Input("Source Name", DefaultString = "Example")]
            IDiffSpread<string> FInSourceName;

            [Input("Connect")]
            IDiffSpread<bool> FInConnect;

            [Input("Update", IsBang = true)]
            ISpread<bool> FInUpdate;


            [Output("Texture Out")]
            ISpread<DX11Resource<DX11DynamicTexture2D>> FOutTexture;

            [Output("Width")]
            ISpread<int> FOutWidth;

            [Output("Height")]
            ISpread<int> FOutHeight;

            [Output("Buffer Size")]
            ISpread<int> FOutBufferSize;

            [Output("Key")]
            ISpread<string> FOutKey;

            //[Output("Format")]
            //ISpread<string> FOutFormat;

            [Import()]
            public ILogger FLogger;

            //Byte[] buffer = null;
            IntPtr buffer_ptr = IntPtr.Zero;
            int width = 0;
            int height = 0;
            int bufferSize;
            bool initialized = false;
            bool disposed = false;

            public void OnImportsSatisfied()
            {
                // Not required, but "correct". (see the SDK documentation)
                if (!NDIlib.initialize())
                {
                    // Cannot run NDI. Most likely because the CPU is not sufficient (see SDK documentation).
                    // you can check this directly with a call to NDIlib_is_supported_CPU()
                    FLogger.Log(LogType.Error, "Cannot run NDI");
                }
                else
                {
                    FLogger.Log(LogType.Message, "is_supported_CPU: " + NDIlib.is_supported_CPU());
                    FLogger.Log(LogType.Message, Marshal.PtrToStringAnsi(NDIlib.version()));

                    initialized = true;
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
                        disposed = true;

                        // Not required, but "correct". (see the SDK documentation)
                        NDIlib.destroy();
                    }
                }
            }

            public void Evaluate(int SpreadMax)
            {

            }

            unsafe public void Update(DX11RenderContext context)
            {

            }

            public void Destroy(DX11RenderContext context, bool force)
            {

            }
        }
    }
}