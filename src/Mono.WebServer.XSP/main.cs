//
// Mono.WebServer.XSP/main.cs: Web Server that uses ASP.NET hosting
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2002,2003 Ximian, Inc (http://www.ximian.com)
// (C) Copyright 2004-2009 Novell, Inc. (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mono.WebServer.XSP
{
	public class Server : MarshalByRefObject
	{
		static RSA key;

		static AsymmetricAlgorithm GetPrivateKey (X509Certificate certificate, string targetHost) 
		{ 
			return key;
		}

		public static void CurrentDomain_UnhandledException (object sender, UnhandledExceptionEventArgs e)
		{
			var ex = (Exception)e.ExceptionObject;

			Console.WriteLine ("Handling exception type {0}", ex.GetType ().Name);
			Console.WriteLine ("Message is {0}", ex.Message);
			Console.WriteLine ("IsTerminating is set to {0}", e.IsTerminating);
			if (e.IsTerminating)
				Console.WriteLine (ex);
		}

		public static int Main (string [] args)
		{
			AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
			bool quiet = false;
			while (true) {
				try {
					return new Server ().RealMain (args, true, null, quiet);
				} catch (ThreadAbortException ex) {
					Console.WriteLine (ex);
					// Single-app mode and ASP.NET appdomain unloaded
					Thread.ResetAbort ();
					quiet = true; // hush 'RealMain'
				}
			}
		}

		//
		// Parameters:
		//
		//   args - original args passed to the program
		//   root - true means caller is in the root domain
		//   ext_apphost - used when single app mode is used, in a recursive call to
		//        RealMain from the single app domain
		//   quiet - don't show messages. Used to avoid double printing of the banner
		//
		public int RealMain (string [] args, bool root, IApplicationHost ext_apphost, bool quiet)
		{
			var configurationManager = new ConfigurationManager (quiet);
			var security = new SecurityConfiguration ();
			
			if (!ParseOptions (configurationManager, args, security))
				return 1;

			// Show the help and exit.
			if (configurationManager.Help) {
				configurationManager.PrintHelp ();
#if DEBUG
				Console.WriteLine ("Press any key...");
				Console.ReadKey ();
#endif
				return 0;
			}

			// Show the version and exit.
			if (configurationManager.Version) {
				Version.Show ();
				return 0;
			}

			WebSource webSource;
			if (security.Enabled) {
				try {
					key = security.KeyPair;
					webSource = new XSPWebSource (configurationManager.Address,
						configurationManager.Port, security.Protocol, 
						security.ServerCertificate, 
						GetPrivateKey, 
						security.AcceptClientCertificates,
						security.RequireClientCertificates,
						!root);
				}
				catch (CryptographicException ce) {
					Console.WriteLine (ce.Message);
					return 1;
				}
			} else {
				webSource = new XSPWebSource (configurationManager.Address, configurationManager.Port, !root);
			}

			var server = new ApplicationServer (webSource, configurationManager.Root) {
				Verbose = configurationManager.Verbose,
				SingleApplication = !root
			};

			if (configurationManager.Applications != null)
				server.AddApplicationsFromCommandLine (configurationManager.Applications);

			if (configurationManager.AppConfigFile != null)
				server.AddApplicationsFromConfigFile (configurationManager.AppConfigFile);

			if (configurationManager.AppConfigDir != null)
				server.AddApplicationsFromConfigDirectory (configurationManager.AppConfigDir);

			if (configurationManager.Applications == null && configurationManager.AppConfigDir == null && configurationManager.AppConfigFile == null)
				server.AddApplicationsFromCommandLine ("/:.");


			VPathToHost vh = server.GetSingleApp ();
			if (root && vh != null) {
				// Redo in new domain
				vh.CreateHost (server, webSource);
				var svr = (Server) vh.AppHost.Domain.CreateInstanceAndUnwrap (GetType ().Assembly.GetName ().ToString (), GetType ().FullName);
				webSource.Dispose ();
				return svr.RealMain (args, false, vh.AppHost, configurationManager.Quiet);
			}
			server.AppHost = ext_apphost;

			if (!configurationManager.Quiet) {
				Console.WriteLine (Assembly.GetExecutingAssembly ().GetName ().Name);
				Console.WriteLine ("Listening on address: {0}", configurationManager.Address);
				Console.WriteLine ("Root directory: {0}", configurationManager.Root);
			}

			try {
				if (server.Start (!configurationManager.NonStop, (int)configurationManager.Backlog) == false)
					return 2;

				if (!configurationManager.Quiet) {
					// MonoDevelop depends on this string. If you change it, let them know.
					Console.WriteLine ("Listening on port: {0} {1}", server.Port, security);
				}
				if (configurationManager.Port == 0 && !configurationManager.Quiet)
					Console.Error.WriteLine ("Random port: {0}", server.Port);
				
				if (!configurationManager.NonStop) {
					if (!configurationManager.Quiet)
						Console.WriteLine ("Hit Return to stop the server.");

					while (true) {
						bool doSleep;
						try {
							Console.ReadLine ();
							break;
						} catch (IOException) {
							// This might happen on appdomain unload
							// until the previous threads are terminated.
							doSleep = true;
						} catch (ThreadAbortException) {
							doSleep = true;
						}

						if (doSleep)
							Thread.Sleep (500);
					}
					server.Stop ();
				}
			} catch (Exception e) {
				if (!(e is ThreadAbortException))
					Console.WriteLine ("Error: {0}", e);
				else
					server.ShutdownSockets ();
				return 1;
			}

			return 0;
		}

		static bool ParseOptions (ConfigurationManager manager, string[] args, SecurityConfiguration security)
		{
			if (!manager.LoadCommandLineArgs (args))
				return false;
			
			// TODO: add mutual exclusivity rules
			if(manager.Https)
				security.Enabled = true;

			if (manager.HttpsClientAccept) {
				security.Enabled = true;
				security.AcceptClientCertificates = true;
				security.RequireClientCertificates = false;
			}

			if (manager.HttpsClientRequire) {
				security.Enabled = true;
				security.AcceptClientCertificates = true;
				security.RequireClientCertificates = true;
			}

			if (manager.P12File != null)
				security.Pkcs12File = manager.P12File;

			if(manager.Cert != null)
				security.CertificateFile = manager.Cert;

			if (manager.PkFile != null)
				security.PvkFile = manager.PkFile;

			if (manager.PkPwd != null)
				security.Password = manager.PkPwd;

			security.Protocol = manager.Protocols;

			int minThreads = manager.MinThreads ?? 0;
			if(minThreads > 0)
				ThreadPool.SetMinThreads (minThreads, minThreads);

			if(!String.IsNullOrEmpty(manager.PidFile))
				try {
					using (StreamWriter sw = File.CreateText (manager.PidFile)) {
						sw.Write (Process.GetCurrentProcess ().Id);
					}
				} catch (Exception ex) {
					Console.Error.WriteLine ("Failed to write pidfile {0}: {1}", manager.PidFile,
						ex.Message);
				}
				
			if(manager.NoHidden)
				MonoWorkerRequest.CheckFileAccess = false;
				
			return true;
		}

		public override object InitializeLifetimeService ()
		{
			return null;
		}
	}
}

