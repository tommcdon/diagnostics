// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Remoting;

namespace SOS.Extensions
{
    public static class HostServices
    {
        private static readonly object s_lock = new object();
        private static AppDomain s_domain;

        public static int Initialize(string extensionPath)
        {
            lock (s_lock)
            {
                string bootstrapPath = typeof(HostServices).Assembly.Location;
                string payloadPath = Path.Combine(Path.GetDirectoryName(bootstrapPath), "isolated");
                if (s_domain == null)
                {
                    AppDomainSetup setup = new AppDomainSetup
                    {
                        ApplicationBase = payloadPath,
                        ConfigurationFile = Path.Combine(payloadPath, "SOS.Extensions.dll.config")
                    };
                    // Native SOS retains callbacks into this domain until process exit.
                    s_domain = AppDomain.CreateDomain("SOS.Framework.Isolated", null, setup);
                }

                // Native hosting and remoting can use different load contexts. Share only the mscorlib contract.
                IObjectHandle initializer = (IObjectHandle)s_domain.CreateInstanceFromAndUnwrap(
                    bootstrapPath, typeof(FrameworkInitializer).FullName, false,
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new object[] { extensionPath, payloadPath }, null, null);
                return (int)initializer.Unwrap();
            }
        }
    }

    public sealed class FrameworkInitializer : MarshalByRefObject, IObjectHandle
    {
        private readonly int _result;

        public FrameworkInitializer(string extensionPath, string payloadPath)
        {
            _result = Initialize(extensionPath, payloadPath);
        }

        public object Unwrap()
        {
            return _result;
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }

        private int Initialize(string extensionPath, string payloadPath)
        {
            Log("HOST Domain=" + AppDomain.CurrentDomain.FriendlyName +
                " IsDefault=" + AppDomain.CurrentDomain.IsDefaultAppDomain() +
                " ApplicationBase=" + AppDomain.CurrentDomain.BaseDirectory);
            AppDomain.CurrentDomain.AssemblyLoad += (sender, e) =>
                Log("ASSEMBLY_LOAD " + e.LoadedAssembly.FullName + " " + e.LoadedAssembly.Location);
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
                Log("FIRST_CHANCE " + e.Exception);

            Assembly assembly = Assembly.LoadFrom(Path.Combine(payloadPath, "SOS.Extensions.dll"));
            Type hostServicesType = assembly.GetType("SOS.Extensions.HostServices", true);
            MethodInfo initialize = hostServicesType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
            int result;
            try
            {
                result = (int)initialize.Invoke(null, new object[] { extensionPath });
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            Log("INITIALIZE_RESULT 0x" + result.ToString("X8"));
            foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Log("LOADED " + loadedAssembly.FullName + " " + loadedAssembly.Location);
            }
            return result;
        }

        private static void Log(string message)
        {
            string logPath = Path.Combine(Path.GetDirectoryName(typeof(FrameworkInitializer).Assembly.Location),
                "appdomain-bootstrap.log");
            File.AppendAllText(logPath, message + Environment.NewLine);
            Trace.WriteLine(message);
        }
    }
}
