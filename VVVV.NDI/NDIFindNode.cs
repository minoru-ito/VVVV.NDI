using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel.Composition;
using VVVV.Core.Logging;
using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using NewTek;
using NewTek.NDI;

namespace VVVV.DX11.Nodes
{
    namespace VVVV.NDI
    {
        [PluginInfo(Name = "Find", Category = "NDI")]
        public class NDIFindNode : IPluginEvaluate, IPartImportsSatisfiedNotification, IDisposable
        {
            [Input("Update", IsBang = true)]
            ISpread<bool> FInUpdate;


            [Output("Source")]
            ISpread<Source> FOutSource;

            [Output("Name")]
            ISpread<string> FOutName;

            [Output("Count")]
            ISpread<int> FOutCount;

            [Output("Version", Visibility = PinVisibility.Hidden)]
            ISpread<string> FOutVersion;
            
            [Output("Initialized", Visibility = PinVisibility.Hidden)]
            ISpread<bool> FOutInitialized;

            [Import()]
            public ILogger FLogger;
            
            bool disposed = false;

            Finder _findInstance;

            public void OnImportsSatisfied()
            {
                FOutSource.SliceCount = 0;
                FOutName.SliceCount = 0;
                FOutCount[0] = 0;
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

                    _findInstance = new Finder(true);
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
                        if (_findInstance != null)
                            _findInstance.Dispose();

                        // Not required, but "correct". (see the SDK documentation)
                        NDIlib.destroy();

                        disposed = true;
                    }
                }
            }

            public void Evaluate(int SpreadMax)
            {
                if(FInUpdate[0])
                {
                    if (_findInstance != null)
                    {
                        ObservableCollection<Source> sources = _findInstance.Sources;
                        if (sources.Count > 0)
                        {
                            //FLogger.Log(LogType.Debug, sources[0].ComputerName + "," + sources[0].Name + "," + sources[0].SourceName);
                            FOutSource.SliceCount = sources.Count;
                            FOutName.SliceCount = sources.Count;
                            FOutCount[0] = sources.Count;

                            for(int i =0; i< sources.Count; i++)
                            {
                                FOutSource[i] = sources[i];
                                FOutName[i] = sources[i].Name;
                            }
                        }
                        else
                        {
                            FOutSource.SliceCount = 0;
                            FOutName.SliceCount = 0;
                            FOutCount[0] = 0;
                        }
                    }
                    else
                    {
                        FOutSource.SliceCount = 0;
                        FOutName.SliceCount = 0;
                        FOutCount[0] = 0;
                    }
                }
            }
        }
    }
}