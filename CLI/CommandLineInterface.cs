//
// Copyright (c) Antmicro
// Copyright (c) Realtime Embedded
//
// This file is part of the Emul8 project.
// Full license details are defined in the 'LICENSE' file.
//
using System;
using Emul8.Logging;
using Emul8.Core;
using Emul8.UserInterface;
using Emul8.Backends.Terminals;
using AntShell;
using AntShell.Terminal;
using Emul8.Exceptions;
using Emul8.Utilities;
using Emul8.Peripherals.UART;
using System.Linq;
using Antmicro.OptionsParser;
using System.IO;

namespace Emul8.CLI
{
    public static class CommandLineInterface
    {
        public static void Run(Options options, Action<ObjectCreator.Context> beforeRun = null)
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => CrashHandler.HandleCrash((Exception)e.ExceptionObject);
            XwtProvider xwt = null;
            try
            {
                if(!options.DisableXwt)
                {
                    xwt = new XwtProvider(new WindowedUserInterfaceProvider());
                }
                using(var context = ObjectCreator.Instance.OpenContext())
                {
                    var monitor = new Emul8.UserInterface.Monitor();
                    context.RegisterSurrogate(typeof(Emul8.UserInterface.Monitor), monitor);

                    // we must initialize plugins AFTER registering monitor surrogate
                    // as some plugins might need it for construction
                    TypeManager.Instance.PluginManager.Init("CLI");

                    if(!options.HideLog)
                    {
                        Logger.AddBackend(ConsoleBackend.Instance, "console");
                    }

                    EmulationManager.Instance.ProgressMonitor.Handler = new CLIProgressMonitor();

                    if(options.Port == -1)
                    {
                        EmulationManager.Instance.CurrentEmulation.BackendManager.SetPreferredAnalyzer(typeof(UARTBackend), typeof(ConsoleWindowBackendAnalyzer));
                        EmulationManager.Instance.EmulationChanged += () =>
                        {
                            EmulationManager.Instance.CurrentEmulation.BackendManager.SetPreferredAnalyzer(typeof(UARTBackend), typeof(ConsoleWindowBackendAnalyzer));
                        };
                    }

                    var shell = PrepareShell(options, monitor);
                    new System.Threading.Thread(x => shell.Start(true))
                    {
                        IsBackground = true,
                        Name = "Shell thread"
                    }.Start();

                    Emulator.BeforeExit += () =>
                    {
                        Emulator.DisposeAll();
                    };

                    if(beforeRun != null)
                    {
                        beforeRun(context);
                    }

                    Emulator.WaitForExit();
                }
            }
            finally
            {
                if(xwt != null)
                {
                    xwt.Dispose();
                }
            }
        }

        private static Shell PrepareShell(Options options, Monitor monitor)
        {
            Shell shell = null;
            if(options.Port >= 0)
            {
                var io = new IOProvider { Backend = new SocketIOSource(options.Port) };
                shell = ShellProvider.GenerateShell(io, monitor, true, false);
            }
            else
            {
                ConsoleWindowBackendAnalyzer terminal = null;
                IOProvider io;
                if(options.HideMonitor)
                {
                    io = new IOProvider { Backend = new DummyIOSource() };
                }
                else
                {
                    terminal = new ConsoleWindowBackendAnalyzer();
                    io = terminal.IO;
                }

                // forcing vcursor is necessary, because calibrating will never end if the window is not shown
                shell = ShellProvider.GenerateShell(io, monitor, forceVCursor: options.HideMonitor);
                monitor.Quitted += shell.Stop;

                if(terminal != null)
                {
                    try
                    {
                        terminal.Quitted += Emulator.Exit;
                        terminal.Show();
                    }
                    catch(InvalidOperationException ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine(ex.Message);
                        Emulator.Exit();
                    }
                }
            }
            shell.Quitted += Emulator.Exit;

            monitor.Interaction = shell.Writer;
            monitor.MachineChanged += emu => shell.SetPrompt(emu != null ? new Prompt(string.Format("({0}) ", emu), ConsoleColor.DarkYellow) : null);

            if(options.Execute != null)
            {
                shell.Started += s => s.InjectInput(string.Format("{0}\n", options.Execute));
            }
            else if(!string.IsNullOrEmpty(options.ScriptPath))
            {
                shell.Started += s => s.InjectInput(string.Format("i {0}{1}\n", Path.IsPathRooted(options.ScriptPath) ? "@" : "$CWD/", options.ScriptPath));
            }

            return shell;
        }

        private class DummyIOSource : IPassiveIOSource
        {
            public void CancelRead()
            {
            }

            public void Dispose()
            {
            }

            public void Flush()
            {
            }

            public int Read()
            {
                return 0;
            }

            public void Write(byte b)
            {
            }
        }
    }
}
