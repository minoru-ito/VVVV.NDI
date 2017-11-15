using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
using VVVV.Core.Logging;
using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using NewTek;
using NewTek.NDI;

namespace VVVV.DX11.Nodes
{
    namespace VVVV.NDI
    {
        [PluginInfo(Name = "Tester", Category = "NDI")]
        public class TesterNode : IPluginEvaluate, IPartImportsSatisfiedNotification, IDisposable
        {
            [Import()]
            ILogger FLogger;

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
                if(disposing)
                {
                    if(!disposed)
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
        }
    }
}
