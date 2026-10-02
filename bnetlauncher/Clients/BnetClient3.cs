using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using bnetlauncher.Utils;
using Interop.UIAutomationClient;

namespace bnetlauncher.Clients
{
    // This client is yet another iteration, extending BnetClient2 to support
    // UIA3 native UI automation so we can more easily detect and interact with
    // Battle.net's ever-changing GUI in a more sane way.
    // You can use tools like FlaUInspect in UIA3 mode to find the names for the
    // gamesdb.ini file.
    class BnetClient3 : BnetClient
    {
        // Standard UIA IDs. Constants avoid embedding the interop library's static classes.
        private const int ControlTypePropertyId = 30003;
        private const int ButtonControlTypeId = 50000;
        private const int InvokePatternId = 10000;

        // UIA_E_ELEMENTNOTENABLED: the control is currently disabled.
        private const int UiaElementNotEnabled = unchecked((int)0x80040200);
        // UIA_E_ELEMENTNOTAVAILABLE: the element no longer exists or is virtualized.
        private const int UiaElementNotAvailable = unchecked((int)0x80040201);

        public BnetClient3()
        {
            Id = "battlenet3";
        }

        public override bool Launch(string cmd)
        {
            Logger.Error("Battle.net UI Automation requires a game's playbutton name pattern.");
            return false;
        }

        /// <summary>
        /// Launches a battle.net client game using it's product code.
        ///
        /// Using the product code will open the client window on the apropriate
        /// tab after which UIA3 can search for the associated "Play" button's text.
        ///
        /// </summary>
        /// <param name="game">Game configuration used to start the game.</param>
        /// <returns>True if the game was successfully launched using UI Automation.</returns>
        public override bool Launch(Game game)
        {
            if (game == null || String.IsNullOrWhiteSpace(game.PlayButtonName))
            {
                Logger.Error("Missing playbutton name pattern for Battle.net UI Automation.");
                return false;
            }

            // Keep COM automation on a dedicated MTA thread, away from the launcher UI.
            bool result = false;

            var worker = new Thread(() => result = LaunchWithUiAutomation(game));
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            worker.Join();

            return result;
        }

        /// <summary>
        /// Launches a battle.net client game using UI Automation to interact with the "Play" button.
        /// </summary>
        /// <param name="game">Game configuration used to start the game.</param>
        /// <returns>True if the game was successfully launched using UI Automation.</returns>
        private bool LaunchWithUiAutomation(Game game)
        {
            IUIAutomation automation = null;
            IUIAutomation2 automation2 = null;
            IUIAutomationCondition buttonCondition = null;

            try
            {
                var namePattern = new Regex("\\A" + Regex.Escape(game.PlayButtonName.Trim())
                    .Replace("\\|", "|")
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "\\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    TimeSpan.FromSeconds(1));

                var bnetLaunchParams = $@"--game={game.Cmd} --productcode={game.Cmd}";

                // We don't use the path product path anymore because the launch
                // happens fully through Battle.net!
                using (var procStarter = Process.Start(Path.Combine(InstallPath, Exe), bnetLaunchParams))
                {
                    // The actual client may stay alive; never wait indefinitely for its exit.
                    procStarter.WaitForExit(5000);
                }

                automation = new CUIAutomation8();
                automation2 = (IUIAutomation2)automation;
                automation2.ConnectionTimeout = 2000;
                automation2.TransactionTimeout = 3000;

                buttonCondition = automation.CreatePropertyCondition(ControlTypePropertyId, ButtonControlTypeId);

                Logger.Information($"Looking for UIA 'Play' button matching '{game.PlayButtonName}'.");

                // Maybe Battle.net is already open, maybe it is still starting...
                // Because we don't know and because navigation within the application takes
                // a while, we allow for a delay in the appearance of the relevant 'Play' button.
                var timer = Stopwatch.StartNew();
                bool waitingForButton = false;

                while (timer.Elapsed < TimeSpan.FromMinutes(1))
                {
                    var matches = new List<IUIAutomationElement>();
                    bool scanComplete = true;

                    try
                    {
                        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe)))
                        {
                            using (process)
                            {
                                IUIAutomationElement window = null;
                                IUIAutomationElementArray buttons = null;

                                try
                                {
                                    IntPtr handle = process.MainWindowHandle;

                                    if (handle == IntPtr.Zero || process.MainWindowTitle != "Battle.net")
                                        continue;

                                    WinApi.NativeMethods.ShowWindow(handle, WinApi.NativeMethods.SW_RESTORE);
                                    
                                    window = automation.ElementFromHandle(handle);
                                    buttons = window.FindAll(TreeScope.TreeScope_Descendants, buttonCondition);
                                    
                                    for (int i = 0; i < buttons.Length; i++)
                                    {
                                        IUIAutomationElement button = buttons.GetElement(i);
                                        try
                                        {
                                            if (namePattern.IsMatch(button.CurrentName ?? String.Empty) && button.CurrentIsOffscreen == 0)
                                            {
                                                Logger.Information($"Matched UIA button: {button.CurrentName}");
                                                matches.Add(button);
                                                button = null;
                                            }
                                        }
                                        finally
                                        {
                                            Release(button);
                                        }
                                    }
                                }
                                catch (COMException)
                                {
                                    // Navigation or a client restart can invalidate the tree; query it again.
                                    scanComplete = false;
                                }
                                catch (InvalidOperationException)
                                {
                                    // A process may have exited while its window was being inspected.
                                    scanComplete = false;
                                }
                                finally
                                {
                                    Release(buttons);
                                    Release(window);
                                }
                            }
                        }

                        if (scanComplete && matches.Count > 1)
                        {
                            Logger.Error($"Multiple UIA buttons match '{game.PlayButtonName}'. Specify a more precise playbutton pattern (for example, include the version).");
                            return false;
                        }

                        if (scanComplete && matches.Count == 1 && matches[0].CurrentIsEnabled != 0 && matches[0].CurrentIsOffscreen == 0)
                        {
                            var invoke = matches[0].GetCurrentPattern(InvokePatternId) as IUIAutomationInvokePattern;

                            if (invoke == null)
                            {
                                Logger.Error("The matching UIA button does not support InvokePattern.");
                                return false;
                            }

                            try
                            {
                                string buttonName = matches[0].CurrentName;
                                invoke.Invoke();
                                Logger.Information($"Invoked UIA button '{buttonName}'.");
                                return true;
                            }
                            catch (COMException ex) when (ex.ErrorCode == UiaElementNotEnabled || ex.ErrorCode == UiaElementNotAvailable)
                            {
                                // Battle.net can report IsEnabled while Invoke still rejects the button
                                // during navigation. Retry only explicit disabled/stale-element errors.
                                if (!waitingForButton)
                                {
                                    Logger.Information("UIA Play button is not ready yet; waiting for navigation to finish.");
                                    waitingForButton = true;
                                }
                            }
                            catch (COMException ex)
                            {
                                Logger.Error("UIA InvokePattern failed; the button will not be invoked again.", ex);
                                return false;
                            }
                            finally
                            { 
                                Release(invoke);
                            }
                        }
                    }
                    catch (COMException)
                    {
                        // A stale or temporarily disabled button can be retried after navigation.
                    }
                    finally
                    {
                        foreach (var match in matches)
                        {
                            Release(match);
                        }
                    }

                    // Wait a bit before retrying to give the UI time to update.
                    Thread.Sleep(500);
                }

                Logger.Error($"Timed out waiting for an enabled UIA Play button matching '{game.PlayButtonName}'.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Couldn't start game using UI Automation for '{game.Cmd}'.", ex);
                return false;
            }
            finally
            {
                Release(buttonCondition);
                Release(automation);
            }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }
    }
}
