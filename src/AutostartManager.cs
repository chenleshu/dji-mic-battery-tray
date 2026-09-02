using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using Microsoft.Win32;

namespace DjiMicBattery
{
    internal sealed class AutostartManager
    {
        internal const string TaskName = "大疆麦克风电量";
        internal const string LaunchArgument = "--autostart";

        private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const int TaskCreateOrUpdate = 6;
        private const int TaskActionExec = 0;
        private const int TaskTriggerLogon = 9;
        private const int TaskLogonInteractiveToken = 3;
        private const int TaskRunLevelLua = 0;
        private const int TaskInstancesIgnoreNew = 2;
        private const uint ErrorFileNotFound = 0x80070002;

        private readonly string executablePath;
        private readonly string workingDirectory;
        private readonly string currentUserName;
        private readonly string currentUserSid;

        internal AutostartManager(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("可执行文件路径不能为空。", "executablePath");
            }

            this.executablePath = Path.GetFullPath(executablePath);
            workingDirectory = Path.GetDirectoryName(this.executablePath);
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                currentUserName = identity.Name;
                currentUserSid = identity.User.Value;
            }
        }

        internal bool IsEnabled()
        {
            object service = null;
            object folder = null;
            object registeredTask = null;
            try
            {
                service = Connect();
                folder = ((dynamic)service).GetFolder("\\");
                registeredTask = ((dynamic)folder).GetTask("\\" + TaskName);
                dynamic task = registeredTask;
                return (bool)task.Enabled && DefinitionMatches(
                    (string)task.Xml,
                    executablePath,
                    currentUserName,
                    currentUserSid
                );
            }
            catch (Exception ex)
            {
                if (IsTaskMissingException(ex))
                {
                    return false;
                }
                throw;
            }
            finally
            {
                Release(registeredTask);
                Release(folder);
                Release(service);
            }
        }

        internal void SetEnabled(bool enabled)
        {
            RemoveLegacyRunEntry();

            if (enabled)
            {
                Register();
                if (!IsEnabled())
                {
                    throw new InvalidOperationException("Windows 登录任务已创建，但完整性校验未通过。");
                }
            }
            else
            {
                Delete();
                if (IsEnabled())
                {
                    throw new InvalidOperationException("Windows 登录任务未能删除。");
                }
            }

        }

        internal void Run()
        {
            if (!IsEnabled())
            {
                throw new InvalidOperationException("Windows 登录任务未启用或配置不完整。");
            }

            object service = null;
            object folder = null;
            object registeredTask = null;
            object runningTask = null;
            try
            {
                service = Connect();
                folder = ((dynamic)service).GetFolder("\\");
                registeredTask = ((dynamic)folder).GetTask("\\" + TaskName);
                runningTask = ((dynamic)registeredTask).Run(null);
            }
            finally
            {
                Release(runningTask);
                Release(registeredTask);
                Release(folder);
                Release(service);
            }
        }

        internal void RemoveLegacyRunEntry()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, true))
            {
                if (key != null)
                {
                    string command = key.GetValue(TaskName) as string;
                    if (IsManagedLegacyRunCommand(command, executablePath))
                    {
                        key.DeleteValue(TaskName, false);
                    }
                }
            }
        }

        internal static bool IsManagedLegacyRunCommand(string command, string currentExecutablePath)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            string candidate = command.Trim();
            if (candidate.Length >= 2 && candidate[0] == '"' && candidate[candidate.Length - 1] == '"')
            {
                candidate = candidate.Substring(1, candidate.Length - 2);
            }

            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return PathsEqual(candidate, currentExecutablePath) ||
                PathsEqual(candidate, Path.Combine(localApplicationData, "DjiMicBatteryTray", "DjiMicBatteryTray.exe")) ||
                PathsEqual(candidate, Path.Combine(localApplicationData, "大疆麦克风电量", "大疆麦克风电量.exe"));
        }

        private void Register()
        {
            object service = null;
            object folder = null;
            object definition = null;
            object registrationInfo = null;
            object settings = null;
            object principal = null;
            object triggers = null;
            object trigger = null;
            object actions = null;
            object action = null;
            object registeredTask = null;
            try
            {
                service = Connect();
                dynamic scheduler = service;
                folder = scheduler.GetFolder("\\");
                definition = scheduler.NewTask(0);
                dynamic taskDefinition = definition;

                registrationInfo = taskDefinition.RegistrationInfo;
                ((dynamic)registrationInfo).Author = "chenleshu";
                ((dynamic)registrationInfo).Description = "登录 Windows 后自动启动大疆麦克风电量托盘程序。";

                settings = taskDefinition.Settings;
                dynamic taskSettings = settings;
                taskSettings.Enabled = true;
                taskSettings.AllowDemandStart = true;
                taskSettings.StartWhenAvailable = true;
                taskSettings.DisallowStartIfOnBatteries = false;
                taskSettings.StopIfGoingOnBatteries = false;
                taskSettings.ExecutionTimeLimit = "PT0S";
                taskSettings.MultipleInstances = TaskInstancesIgnoreNew;

                principal = taskDefinition.Principal;
                dynamic taskPrincipal = principal;
                taskPrincipal.Id = "Author";
                taskPrincipal.UserId = currentUserName;
                taskPrincipal.LogonType = TaskLogonInteractiveToken;
                taskPrincipal.RunLevel = TaskRunLevelLua;

                triggers = taskDefinition.Triggers;
                trigger = ((dynamic)triggers).Create(TaskTriggerLogon);
                dynamic logonTrigger = trigger;
                logonTrigger.Enabled = true;
                logonTrigger.UserId = currentUserName;
                logonTrigger.Delay = "PT10S";

                actions = taskDefinition.Actions;
                action = ((dynamic)actions).Create(TaskActionExec);
                dynamic execAction = action;
                execAction.Path = executablePath;
                execAction.Arguments = LaunchArgument;
                execAction.WorkingDirectory = workingDirectory;

                registeredTask = ((dynamic)folder).RegisterTaskDefinition(
                    TaskName,
                    definition,
                    TaskCreateOrUpdate,
                    currentUserName,
                    null,
                    TaskLogonInteractiveToken,
                    null
                );
            }
            finally
            {
                Release(registeredTask);
                Release(action);
                Release(actions);
                Release(trigger);
                Release(triggers);
                Release(principal);
                Release(settings);
                Release(registrationInfo);
                Release(definition);
                Release(folder);
                Release(service);
            }
        }

        private static void Delete()
        {
            object service = null;
            object folder = null;
            try
            {
                service = Connect();
                folder = ((dynamic)service).GetFolder("\\");
                ((dynamic)folder).DeleteTask(TaskName, 0);
            }
            catch (Exception ex)
            {
                if (!IsTaskMissingException(ex))
                {
                    throw;
                }
            }
            finally
            {
                Release(folder);
                Release(service);
            }
        }

        private static object Connect()
        {
            Type serviceType = Type.GetTypeFromProgID("Schedule.Service", true);
            object service = Activator.CreateInstance(serviceType);
            ((dynamic)service).Connect();
            return service;
        }

        internal static bool DefinitionMatches(
            string xml,
            string expectedExecutablePath,
            string expectedUserName,
            string expectedUserSid
        )
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return false;
            }

            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);
            XmlNamespaceManager namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("t", document.DocumentElement.NamespaceURI);

            XmlNodeList triggerNodes = document.SelectNodes("/t:Task/t:Triggers/*", namespaces);
            if (triggerNodes == null || triggerNodes.Count != 1)
            {
                return false;
            }

            XmlNode logonTrigger = triggerNodes[0];
            if (logonTrigger.LocalName != "LogonTrigger")
            {
                return false;
            }
            if (logonTrigger == null || IsExplicitlyFalse(logonTrigger.SelectSingleNode("t:Enabled", namespaces)))
            {
                return false;
            }

            if (!IdentityMatches(
                NodeText(logonTrigger.SelectSingleNode("t:UserId", namespaces)),
                expectedUserName,
                expectedUserSid
            ))
            {
                return false;
            }

            XmlNodeList principalNodes = document.SelectNodes("/t:Task/t:Principals/t:Principal", namespaces);
            if (principalNodes == null || principalNodes.Count != 1)
            {
                return false;
            }

            XmlNode principal = principalNodes[0];
            if (principal == null || NodeText(principal.SelectSingleNode("t:LogonType", namespaces)) != "InteractiveToken")
            {
                return false;
            }

            if (!IdentityMatches(
                NodeText(principal.SelectSingleNode("t:UserId", namespaces)),
                expectedUserName,
                expectedUserSid
            ))
            {
                return false;
            }

            string runLevel = NodeText(principal.SelectSingleNode("t:RunLevel", namespaces));
            if (!string.IsNullOrEmpty(runLevel) && runLevel != "LeastPrivilege")
            {
                return false;
            }

            XmlNode settings = document.SelectSingleNode("/t:Task/t:Settings", namespaces);
            if (settings == null ||
                NodeText(settings.SelectSingleNode("t:ExecutionTimeLimit", namespaces)) != "PT0S" ||
                NodeText(settings.SelectSingleNode("t:MultipleInstancesPolicy", namespaces)) != "IgnoreNew" ||
                !IsExplicitlyFalse(settings.SelectSingleNode("t:DisallowStartIfOnBatteries", namespaces)) ||
                !IsExplicitlyFalse(settings.SelectSingleNode("t:StopIfGoingOnBatteries", namespaces)))
            {
                return false;
            }

            XmlNodeList actionNodes = document.SelectNodes("/t:Task/t:Actions/*", namespaces);
            if (actionNodes == null || actionNodes.Count != 1)
            {
                return false;
            }

            XmlNode exec = actionNodes[0];
            if (exec.LocalName != "Exec")
            {
                return false;
            }
            if (exec == null ||
                !PathsEqual(NodeText(exec.SelectSingleNode("t:Command", namespaces)), expectedExecutablePath) ||
                NodeText(exec.SelectSingleNode("t:Arguments", namespaces)) != LaunchArgument ||
                !PathsEqual(
                    NodeText(exec.SelectSingleNode("t:WorkingDirectory", namespaces)),
                    Path.GetDirectoryName(Path.GetFullPath(expectedExecutablePath))
                ))
            {
                return false;
            }

            return true;
        }

        internal static bool IsTaskMissingException(Exception failure)
        {
            return failure != null && unchecked((uint)failure.HResult) == ErrorFileNotFound;
        }

        private static bool IdentityMatches(string actual, string expectedName, string expectedSid)
        {
            return string.Equals(actual, expectedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actual, expectedSid, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplicitlyFalse(XmlNode node)
        {
            return node != null && string.Equals(NodeText(node), "false", StringComparison.OrdinalIgnoreCase);
        }

        private static string NodeText(XmlNode node)
        {
            return node == null ? string.Empty : node.InnerText.Trim();
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(left.Trim().Trim('"')),
                    Path.GetFullPath(right.Trim().Trim('"')),
                    StringComparison.OrdinalIgnoreCase
                );
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
    }
}
