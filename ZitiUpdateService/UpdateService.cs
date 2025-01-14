using System;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.IO;
using System.Timers;
using System.Configuration;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.IO.Compression;

using ZitiDesktopEdge.DataStructures;
using ZitiDesktopEdge.ServiceClient;
using ZitiDesktopEdge.Server;
using ZitiDesktopEdge.Utility;

using NLog;
using Newtonsoft.Json;
using System.Net;
using DnsClient;
using ZitiUpdateService.Checkers.PeFile;
using ZitiUpdateService.Utils;

namespace ZitiUpdateService {
	public partial class UpdateService : ServiceBase {
		private string betaStreamMarkerFile = "use-beta-stream.txt";

		public bool IsBeta {
			get {
				return File.Exists(Path.Combine(exeLocation, betaStreamMarkerFile));
			}
			private set { }
		}

		public bool useGithubCheck { get; private set; }

		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		private System.Timers.Timer _updateTimer = new System.Timers.Timer();
		private CustomTimer _installationReminder = null;
		private static SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

		private string exeLocation = null;

		private DataClient dataClient = new DataClient();
		private bool running = false;
		private string asmDir = null;
		private string updateFolder = null;
		private string filePrefix = "Ziti.Desktop.Edge.Client-";
		Version assemblyVersion = null;

		ServiceController controller;
		IPCServer svr = new IPCServer();
		Task ipcServer = null;
		Task eventServer = null;
		Checkers.UpdateCheck check = null;

		private System.Timers.Timer dnsProbeTimer = new System.Timers.Timer();
		private IPAddress dnsIpAddress = null;

		public UpdateService() {
			InitializeComponent();
			base.CanHandlePowerEvent = true;

			useGithubCheck = true; //set this to false if you want to use the FileCheck test class instead of Github

			exeLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

			Logger.Info("Initializing");
			dataClient.OnClientConnected += Svc_OnClientConnected;
			dataClient.OnTunnelStatusEvent += Svc_OnTunnelStatusEvent;
			dataClient.OnClientDisconnected += Svc_OnClientDisconnected;
			dataClient.OnShutdownEvent += Svc_OnShutdownEvent;

			svr.CaptureLogs = CaptureLogs;
			svr.SetLogLevel = SetLogLevel;
			svr.SetReleaseStream = SetReleaseStream;
			svr.DoUpdateCheck = DoUpdateCheck;
			svr.TriggerUpdate = TriggerUpdate;

		}


		private SvcResponse TriggerUpdate() {
			SvcResponse r = new SvcResponse();
			r.Message = "Initiating Update Check";
			checkUpdateImmediately();
			return r;
		}

		private void TriggerUpdateEvent(Object state) {
			TimerState timerState = (TimerState)state;
			Logger.Debug("Timer initiating installation of {0}, exhausted allowed waiting time - {1}", timerState.zdeInstallerInfo.Version.ToString(), timerState._timer.Period);
			checkUpdateImmediately();
			Logger.Warn("If installation didnt complete, then the timer will initiate this function again at the next interval - {0}", timerState._timer.Period);
		}

		private void checkUpdateImmediately() {
			try {
				CheckUpdate(null, null);
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error in CheckUpdate");
			}
		}

		private StatusCheck DoUpdateCheck() {
			StatusCheck r = new StatusCheck();
			int updateAvailable;
			string publishedDate;
			check.CheckUpdate(assemblyVersion, out updateAvailable, out publishedDate);
			r.Code = updateAvailable;
			r.ReleaseStream = IsBeta ? "beta" : "stable";
			switch (r.Code) {
				case -1:
					r.Message = $"An update is available: {check.GetNextVersion()}";
					r.UpdateAvailable = true;
					Logger.Debug("Update {0} is published on {1}", check.GetNextVersion(), publishedDate);
					break;
				case 0:
					r.Message = $"The current version [{assemblyVersion}] is the latest";
					break;
				case 1:
					r.Message = $"Your version [{assemblyVersion}] is newer than the latest release";
					break;
				default:
					r.Message = "Update check failed";
					break;
			}
			return r;
		}

		private void SetLogLevel(string level) {
			try {
				Logger.Info("request to change log level received: {0}", level);
				var l = LogLevel.FromString(level);
				foreach (var rule in LogManager.Configuration.LoggingRules) {
					rule.EnableLoggingForLevel(l);
				}

				LogManager.ReconfigExistingLoggers();
				Logger.Info("logger reconfigured to log at level: {0}", l);
			} catch (Exception e) {
				Logger.Error(e, "Could NOT set the log level for loggers??? {0}", e.Message);
			}
		}

		private void SetReleaseStream(string stream) {
			string markerFile = Path.Combine(exeLocation, betaStreamMarkerFile);
			if (stream == "beta") {
				if (IsBeta) {
					Logger.Debug("already using beta stream. No action taken");
				} else {
					Logger.Info("Setting update service to use beta stream!");
					using (File.Create(markerFile)) {

					}
					AccessUtils.GrantAccessToFile(markerFile); //allow anyone to delete this manually if need be...
					Logger.Debug("added marker file: {0}", markerFile);
					ConfigureCheck();
				}
			} else {
				if (!IsBeta) {
					Logger.Debug("already using release stream. No action taken");
				} else {
					Logger.Info("Setting update service to use release stream!");
					if (File.Exists(markerFile)) {
						File.Delete(markerFile);
						Logger.Debug("removed marker file: {0}", markerFile);
					}
					ConfigureCheck();
				}
			}
		}

		private string CaptureLogs() {
			try {
				string logLocation = Path.Combine(exeLocation, "logs");
				string destinationLocation = Path.Combine(exeLocation, "temp");
				string serviceLogsLocation = Path.Combine(logLocation, "service");
				string serviceLogsDest = Path.Combine(destinationLocation, "service");

				Logger.Debug("removing leftover temp folder: {0}", destinationLocation);
				try {
					Directory.Delete(destinationLocation, true);
				} catch {
					//means it doesn't exist
				}

				Directory.CreateDirectory(destinationLocation);

				Logger.Debug("copying all directories from: {0}", logLocation);
				foreach (string dirPath in Directory.GetDirectories(logLocation, "*", SearchOption.AllDirectories)) {
					Directory.CreateDirectory(dirPath.Replace(logLocation, destinationLocation));
				}

				Logger.Debug("copying all non-zip files from: {0}", logLocation);
				foreach (string newPath in Directory.GetFiles(logLocation, "*.*", SearchOption.AllDirectories)) {
					if (!newPath.EndsWith(".zip")) {
						File.Copy(newPath, newPath.Replace(logLocation, destinationLocation), true);
					}
				}

				Logger.Debug("copying service files from: {0} to {1}", serviceLogsLocation, serviceLogsDest);
				Directory.CreateDirectory(serviceLogsDest);
				foreach (string newPath in Directory.GetFiles(serviceLogsLocation, "*.*", SearchOption.TopDirectoryOnly)) {
					if (newPath.EndsWith(".log") || newPath.Contains("config.json")) {
						Logger.Debug("copying service log: {0}", newPath);
						File.Copy(newPath, newPath.Replace(serviceLogsLocation, serviceLogsDest), true);
					}
				}

				outputIpconfigInfo(destinationLocation);
				outputSystemInfo(destinationLocation);
				outputDnsCache(destinationLocation);
				outputExternalIP(destinationLocation);
				outputTasklist(destinationLocation);
				outputRouteInfo(destinationLocation);
				outputNetstatInfo(destinationLocation);
				outputNrpt(destinationLocation);

				Task.Delay(500).Wait();

				string zipName = Path.Combine(logLocation, DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".zip");
				ZipFile.CreateFromDirectory(destinationLocation, zipName);

				Logger.Debug("cleaning up temp folder: {0}", destinationLocation);
				try {
					Directory.Delete(destinationLocation, true);
				} catch {
					//means it doesn't exist
				}
				return zipName;
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error in generating system files {0}", ex.Message);
				return null;
			}
		}

		private void outputIpconfigInfo(string destinationFolder) {
			Logger.Info("capturing ipconfig information");
			try {
				Process process = new Process();
				ProcessStartInfo startInfo = new ProcessStartInfo();
				startInfo.WindowStyle = ProcessWindowStyle.Hidden;
				startInfo.FileName = "cmd.exe";
				var ipconfigOut = Path.Combine(destinationFolder, "ipconfig.all.txt");
				Logger.Debug("copying ipconfig /all to {0}", ipconfigOut);
				startInfo.Arguments = $"/C ipconfig /all > \"{ipconfigOut}\"";
				process.StartInfo = startInfo;
				process.Start();
				process.WaitForExit();
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error in outputIpconfigInfo {0}", ex.Message);
			}
		}

		private void outputSystemInfo(string destinationFolder) {
			Logger.Info("capturing systeminfo");
			try {
				Process process = new Process();
				ProcessStartInfo startInfo = new ProcessStartInfo();
				startInfo.WindowStyle = ProcessWindowStyle.Hidden;
				startInfo.FileName = "cmd.exe";
				var sysinfoOut = Path.Combine(destinationFolder, "systeminfo.txt");
				Logger.Debug("running systeminfo to {0}", sysinfoOut);
				startInfo.Arguments = $"/C systeminfo > \"{sysinfoOut}\"";
				process.StartInfo = startInfo;
				process.Start();
				process.WaitForExit();
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error in outputSystemInfo {0}", ex.Message);
			}
		}

		private void outputDnsCache(string destinationFolder) {
			Logger.Info("capturing dns cache information");
			try {
				Process process = new Process();
				ProcessStartInfo startInfo = new ProcessStartInfo();
				startInfo.WindowStyle = ProcessWindowStyle.Hidden;
				startInfo.FileName = "cmd.exe";
				var dnsCache = Path.Combine(destinationFolder, "dnsCache.txt");
				Logger.Debug("running ipconfig /displaydns to {0}", dnsCache);
				startInfo.Arguments = $"/C ipconfig /displaydns > \"{dnsCache}\"";
				process.StartInfo = startInfo;
				process.Start();
				process.WaitForExit();
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error in outputDnsCache {0}", ex.Message);
			}
		}

		private void outputExternalIP(string destinationFolder) {
			Logger.Info("capturing external IP address using eth0.me");
			try {
				var extIpFile = Path.Combine(destinationFolder, "externalIP.txt");
				System.Net.Http.HttpClient httpClient = new System.Net.Http.HttpClient();
				var resp = httpClient.GetAsync("http://eth0.me").Result;
				resp.EnsureSuccessStatusCode();
				string responseBody = resp.Content.ReadAsStringAsync().Result;
				File.WriteAllText(extIpFile, responseBody);
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error in outputExternalIP {0}", ex.Message);
			}
		}

		private void outputTasklist(string destinationFolder) {
			Logger.Info("capturing executing tasks");
			try {
				Process process = new Process();
				ProcessStartInfo startInfo = new ProcessStartInfo();
				startInfo.WindowStyle = ProcessWindowStyle.Hidden;
				startInfo.FileName = "cmd.exe";
				var tasklistOutput = Path.Combine(destinationFolder, "tasklist.txt");
				Logger.Debug("running tasklist to {0}", tasklistOutput);
				startInfo.Arguments = $"/C tasklist > \"{tasklistOutput}\"";
				process.StartInfo = startInfo;
				process.Start();
				process.WaitForExit();
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error {0}", ex.Message);
			}
		}

		private void outputRouteInfo(string destinationFolder) {
			Logger.Info("capturing network routes");
			try {
				Process process = new Process();
				ProcessStartInfo startInfo = new ProcessStartInfo();
				startInfo.WindowStyle = ProcessWindowStyle.Hidden;
				startInfo.FileName = "cmd.exe";
				var networkRoutes = Path.Combine(destinationFolder, "network-routes.txt");
				Logger.Debug("running route print to {0}", networkRoutes);
				startInfo.Arguments = $"/C route print > \"{networkRoutes}\"";
				process.StartInfo = startInfo;
				process.Start();
				process.WaitForExit();
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error {0}", ex.Message);
			}
		}

		private void outputNetstatInfo(string destinationFolder) {
			Logger.Info("capturing netstat");
			try {
				Process process = new Process();
				ProcessStartInfo startInfo = new ProcessStartInfo();
				startInfo.WindowStyle = ProcessWindowStyle.Hidden;
				startInfo.FileName = "cmd.exe";
				var netstatOutput = Path.Combine(destinationFolder, "netstat.txt");
				Logger.Debug("running netstat -ano to {0}", netstatOutput);
				startInfo.Arguments = $"/C netstat -ano > \"{netstatOutput}\"";
				process.StartInfo = startInfo;
				process.Start();
				process.WaitForExit();
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error {0}", ex.Message);
			}
		}

		private void outputNrpt(string destinationFolder) {
			Logger.Info("outputting NRPT rules");
			try {
				Logger.Info("outputting NRPT DnsClientNrptRule");
				string nrptRuleOutput = Path.Combine(destinationFolder, "NrptRule.txt");
				Process nrptRuleProcess = new Process();
				ProcessStartInfo nrptRuleStartInfo = new ProcessStartInfo();
				nrptRuleStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
				nrptRuleStartInfo.FileName = "cmd.exe";
				nrptRuleStartInfo.Arguments = $"/C powershell \"Get-DnsClientNrptRule | sort -Property Namespace\" > \"{nrptRuleOutput}\"";
				Logger.Info("Running: {0}", nrptRuleStartInfo.Arguments);
				nrptRuleProcess.StartInfo = nrptRuleStartInfo;
				nrptRuleProcess.Start();
				nrptRuleProcess.WaitForExit();

				Logger.Info("outputting NRPT DnsClientNrptPolicy");
				string nrptOutput = Path.Combine(destinationFolder, "NrptPolicy.txt");
				Process process = new Process();
				ProcessStartInfo startInfo = new ProcessStartInfo();
				startInfo.WindowStyle = ProcessWindowStyle.Hidden;
				startInfo.FileName = "cmd.exe";
				startInfo.Arguments = $"/C powershell \"Get-DnsClientNrptPolicy | sort -Property Namespace\" > \"{nrptOutput}\"";
				Logger.Info("Running: {0}", startInfo.Arguments);
				process.StartInfo = startInfo;
				process.Start();
				process.WaitForExit();
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error {0}", ex.Message);
			}
		}

		public void Debug() {
			OnStart(null);// new string[] { "FilesystemCheck" });
		}

		protected override void OnStart(string[] args) {
			Logger.Debug("args: {0}", args);
			Logger.Info("ziti-monitor service is starting");

			var logs = Path.Combine(exeLocation, "logs");
			addLogsFolder(exeLocation);
			addLogsFolder(logs);
			addLogsFolder(Path.Combine(logs, "UI"));
			addLogsFolder(Path.Combine(logs, "ZitiMonitorService"));
			addLogsFolder(Path.Combine(logs, "service"));

			AccessUtils.GrantAccessToFile(Path.Combine(exeLocation, "ZitiUpdateService.exe.config")); //allow anyone to change the config file
			AccessUtils.GrantAccessToFile(Path.Combine(exeLocation, "ZitiUpdateService-log.config")); //allow anyone to change the log file config
			AccessUtils.GrantAccessToFile(Path.Combine(exeLocation, "ZitiDesktopEdge.exe.config")); //allow anyone to change the config file
			AccessUtils.GrantAccessToFile(Path.Combine(exeLocation, "ZitiDesktopEdge-log.config")); //allow anyone to change the log file config

			dnsProbeTimer.Elapsed += DnsProbe_Elapsed;
			dnsProbeTimer.Interval = dnsProbeIntervalInSeconds * 1000;

			Logger.Info("starting ipc server");
			ipcServer = svr.startIpcServerAsync(onIpcClientAsync);
			Logger.Info("starting events server");
			eventServer = svr.startEventsServerAsync(onEventsClientAsync);

			Logger.Info("starting service watchers");
			if (!running) {
				running = true;
				Task.Run(() => {
					SetupServiceWatchers();
				});
			}
			Logger.Info("ziti-monitor service is initialized and running");
			base.OnStart(args);
		}

		IPAddress lh = IPAddress.Parse("127.0.0.1"); //expected result
		int dnsProbeFailCount = 0;
		int dnsProbeIntervalInSeconds = 60;
		bool dnsProbeStarted = false;

        private void DnsProbe_Elapsed(object sender, ElapsedEventArgs e) {
			if (dnsProbeStarted) return; //skip out if it's already going...
			dnsProbeStarted = true;
			Logger.Trace("dns probe started");
			try {
				if (dnsIpAddress != null) {
					DnsQuestion q = new DnsQuestion("dew-dns-probe.openziti.org", QueryType.A);
					var dnsEp = new IPEndPoint(dnsIpAddress, 53);
					var dnsProbe = new LookupClient(dnsEp);

					foreach (DnsClient.Protocol.ARecord arec in dnsProbe.Query(q).AllRecords) {
						if (arec.Address.Equals(lh)) {
							dnsProbeFailCount = 0;
							Logger.Debug("dns probe success");
						} else {
							dnsProbeFailCount++;
							logDnsProbeFailure(null);
						}
					}
				}
			} catch(Exception dnse) {
				//don't really care but it probably means a timeout happened.  but might as well log a trace error anyway...
				//it's expected that this is due to the service shutting down...
				dnsProbeFailCount++;
				logDnsProbeFailure(dnse);
			}
			dnsProbeStarted = false;
		}

		private void logDnsProbeFailure(Exception e) {
			bool logit = false;
			if (dnsProbeFailCount <= 4) {
				logit = true;
			} else {
				//else log it every 5 minutes... 
				logit = dnsProbeFailCount % (5 * 60 / dnsProbeIntervalInSeconds) == 0;
			}
			if (logit) {
				if (e != null) {
					Logger.Warn(e, "dns probe failed due to error. This has happened {0} times", dnsProbeFailCount);
				} else {
					Logger.Warn("dns probe failed. This has happened {0} times", dnsProbeFailCount);
				}
			}
		}

        async private Task onEventsClientAsync(StreamWriter writer) {
			Logger.Info("a new events client was connected");
			//reset to release stream
			//initial status when connecting the event stream
			MonitorServiceStatusEvent status = new MonitorServiceStatusEvent() {
				Code = 0,
				Error = null,
				Message = "Success",
				Status = ServiceActions.ServiceStatus(),
				ReleaseStream = IsBeta ? "beta" : "stable"
			};
			await writer.WriteLineAsync(JsonConvert.SerializeObject(status));
			await writer.FlushAsync();
			if (_installationReminder != null) {
				TimerState state = _installationReminder.State;
				InstallationNotificationEvent notificationMsg = logAndNotifyInstallationUpdates(state.zdeInstallerInfo, true);
				await writer.WriteLineAsync(JsonConvert.SerializeObject(notificationMsg));
				await writer.FlushAsync();
			}
		}

#pragma warning disable 1998 //This async method lacks 'await'
		async private Task onIpcClientAsync(StreamWriter writer) {
			Logger.Info("a new ipc client was connected");
		}
#pragma warning restore 1998 //This async method lacks 'await'

		private void addLogsFolder(string path) {
			if (!Directory.Exists(path)) {
				Logger.Info($"creating folder: {path}");
				Directory.CreateDirectory(path);
				AccessUtils.GrantAccessToDirectory(path);
			}
		}

		public void WaitForCompletion() {
			Task.WaitAll(ipcServer, eventServer);
		}

		protected override void OnStop() {
			Logger.Info("ziti-monitor OnStop was called");
			base.OnStop();
		}

		protected override void OnPause() {
			Logger.Info("ziti-monitor OnPause was called");
			base.OnPause();
		}

		protected override void OnShutdown() {
			Logger.Info("ziti-monitor OnShutdown was called");
			base.OnShutdown();
		}

		protected override void OnContinue() {
			Logger.Info("ziti-monitor OnContinue was called");
			base.OnContinue();
		}

		protected override void OnCustomCommand(int command) {
			Logger.Info("ziti-monitor OnCustomCommand was called {0}", command);
			base.OnCustomCommand(command);
		}

		protected override void OnSessionChange(SessionChangeDescription changeDescription) {
			Logger.Info("ziti-monitor OnSessionChange was called {0}", changeDescription);
			base.OnSessionChange(changeDescription);
		}

		protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus) {
			Logger.Info("ziti-monitor OnPowerEvent was called {0}", powerStatus);
			if (_installationReminder != null) {
				Logger.Info("Installation timer - Power event");
				_installationReminder.UpdateTimer(powerStatus);
			}
			return base.OnPowerEvent(powerStatus);
		}

		private void SetupServiceWatchers() {
			var updateTimerInterval = ConfigurationManager.AppSettings.Get("UpdateTimer");
			var upInt = TimeSpan.Zero;
			if (!TimeSpan.TryParse(updateTimerInterval, out upInt)) {
				upInt = new TimeSpan(0, 1, 0);
			}

			_updateTimer = new System.Timers.Timer();
			_updateTimer.Elapsed += CheckUpdate;
			_updateTimer.Interval = upInt.TotalMilliseconds;
			_updateTimer.Enabled = true;
			_updateTimer.Start();
			Logger.Info("Version Checker is running every {0} minutes", upInt.TotalMinutes);

			//on startup - see if the old wintun software was installed and if so ... try to remove it
			UninstallOpenZitiWintun.DetectAndUninstallOpenZitiWintun();

			string assemblyVersionStr = Assembly.GetExecutingAssembly().GetName().Version.ToString(); //fetch from ziti?
			assemblyVersion = new Version(assemblyVersionStr);
			asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			updateFolder = Path.Combine(asmDir, "updates");
			if (!Directory.Exists(updateFolder)) {
				Directory.CreateDirectory(updateFolder);
			}

			cleanOldLogs(asmDir);
			scanForStaleDownloads(updateFolder);

			ConfigureCheck();

			checkUpdateImmediately();

			try {
				dataClient.ConnectAsync().Wait();
			} catch {
				dataClient.Reconnect();
			}

			dataClient.WaitForConnectionAsync().Wait();
		}

		private void ConfigureCheck() {
			string updateUrl = null;
			string releasesUrl = null;
			if (!IsBeta) {
				updateUrl = "https://api.github.com/repos/openziti/desktop-edge-win/releases/latest"; //hardcoded on purpose
				releasesUrl = GithubAPI.ProdReleasesUrl;
			} else {
				updateUrl = "https://api.github.com/repos/openziti/desktop-edge-win-beta/releases/latest";
				releasesUrl = GithubAPI.BetaReleasesUrl;
			}
			if (useGithubCheck) {
				check = new Checkers.GithubCheck(updateUrl, releasesUrl);
			} else {
				check = new Checkers.FilesystemCheck(1);
			}
		}

		private void cleanOldLogs(string whereToScan) {
			//this function will be removed in the future. it's here to clean out the old ziti-monitor*log files that
			//were there before the 1.5.0 release
			try {
				Logger.Info("Scanning for stale logs");
				foreach (var f in Directory.EnumerateFiles(whereToScan)) {
					FileInfo logFile = new FileInfo(f);
					if (logFile.Name.StartsWith("ziti-monitor.") && logFile.Name.EndsWith(".log")) {
						Logger.Info("removing old log file: " + logFile.Name);
						logFile.Delete();
					}
				}
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error has occurred");
			}
		}

		private void CheckUpdate(object sender, ElapsedEventArgs e) {
			if (e != null) {
				Logger.Debug("Timer triggered CheckUpdate at {0}", e.SignalTime);
			}
			if (check == null) {
				Logger.Warn("update check object is not ready. This is abnormal. Please report if you see this warning");
				return;
			}
			semaphore.Wait();

			try {
				Logger.Debug("checking for update");
				String publishedDate;
				int avail;

				check.CheckUpdate(assemblyVersion, out avail, out publishedDate);
				if (avail >= 0) {
					Logger.Debug("update check complete. no update available");
					semaphore.Release();
					return;
				}

				Logger.Info("update is available.");
				if (!Directory.Exists(updateFolder)) {
					Directory.CreateDirectory(updateFolder);
				}

				Logger.Info("copying update package");
				string filename = check.FileName();

				string fileDestination = Path.Combine(updateFolder, filename);

				if (check.AlreadyDownloaded(updateFolder, filename)) {
					Logger.Trace("package has already been downloaded to {0}", fileDestination);
				} else {
					Logger.Info("copying update package begins");
					check.CopyUpdatePackage(updateFolder, filename);
					Logger.Info("copying update package complete");
				}

				if (sender == null && e == null) {
					installZDE(fileDestination, filename);
				} else {
					Checkers.ZDEInstallerInfo info = check.GetZDEInstallerInfo(fileDestination);

					if (info.IsCritical) {
						info.TimeRemaining = 0;
						info.InstallTime = DateTime.Now;
						NotifyInstallationUpdates(info, 0, "");
						installZDE(fileDestination, filename);
					} else if (!info.IsCritical && _installationReminder == null) {
						// Timer for installation reminder
						var installationReminderInterval = ConfigurationManager.AppSettings.Get("InstallationReminder");
						var instInt = TimeSpan.Zero;
						if (!TimeSpan.TryParse(installationReminderInterval, out instInt)) {
							// if InstallationReminder value is not configured, set it to 1 hour
							instInt = new TimeSpan(1, 0, 0);
						}
						TimerState state = new TimerState();
						state.zdeInstallerInfo = info;
						System.Threading.TimerCallback callback = new System.Threading.TimerCallback(TriggerUpdateEvent);
						// waits for the time updated in instInt field and then triggers at every interval
						_installationReminder = new CustomTimer(callback, state, instInt, instInt);
						state._timer = _installationReminder;

						info.TimeRemaining = _installationReminder.DueTime.TotalMilliseconds / 1000; // converting to seconds
						info.InstallTime = DateTime.Now.AddMilliseconds(_installationReminder.DueTime.TotalMilliseconds);
						Logger.Info("Installation reminder for ZDE version {0} is set to {1}, TimeRemaining - {2}, approximate install time - {3}", info.Version, instInt, info.TimeRemaining, info.InstallTime);

						NotifyInstallationUpdates(info, 0, "");
					}

					if (_installationReminder != null) {
						logAndNotifyInstallationUpdates(info, false);
					}
				}
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected error has occurred during the check for ZDE updates");
			}
			semaphore.Release();
		}

		private InstallationNotificationEvent logAndNotifyInstallationUpdates(Checkers.ZDEInstallerInfo info, bool newClient) {
			info.TimeRemaining = _installationReminder.DueTime.TotalMilliseconds / 1000; // converting to seconds
			info.InstallTime = DateTime.Now.AddMilliseconds(_installationReminder.DueTime.TotalMilliseconds);
			Logger.Info("Installation of ZDE version {0} will be initiated in {1} seconds, approximately at {2}", info.Version, info.TimeRemaining, info.InstallTime);
			if (newClient || (info.TimeRemaining > 0 && (info.TimeRemaining / 1000) < 1)) {
				return NotifyInstallationUpdates(info, 0, "");
			}
			return null;
		}

		private void installZDE(string fileDestination, string filename) {
			Logger.Info("package is in {0} - moving to install phase", fileDestination);

			if (!check.HashIsValid(updateFolder, filename)) {
				Logger.Warn("The file was downloaded but the hash is not valid. The file will be removed: {0}", fileDestination);
				File.Delete(fileDestination);
				return;
			}
			Logger.Debug("downloaded file hash was correct. update can continue.");
			new SignedFileValidator(fileDestination).Verify();

			try {
                StopZiti();
                StopUI().Wait();

                Logger.Info("Running update package: " + fileDestination);
                // shell out to a new process and run the uninstall, reinstall steps which SHOULD stop this current process as well
                Process.Start(fileDestination, "/passive");
		    } catch (Exception ex) {
		        Logger.Error(ex, "Unexpected error during installation");
		    }
		}

		private bool isOlder(Version current) {
			int compare = current.CompareTo(assemblyVersion);
			Logger.Info("comparing current[{0}] to compare[{1}]: {2}", current.ToString(), assemblyVersion.ToString(), compare);
			if (compare < 0) {
				return true;
			} else if (compare > 0) {
				return false;
			} else {
				return false;
			}
		}

		private void scanForStaleDownloads(string folder) {
			try {
				if (!Directory.Exists(folder)) {
					Logger.Debug("folder {0} does not exist. skipping", folder);
					return;
				}
				Logger.Info("Scanning for stale downloads");
				foreach (var f in Directory.EnumerateFiles(folder)) {
					FileInfo fi = new FileInfo(f);
					if (fi.Exists) {
						if (fi.Name.StartsWith(filePrefix)) {
							Logger.Debug("scanning for staleness: " + f);
							string ver = Path.GetFileNameWithoutExtension(f).Substring(filePrefix.Length);
							Version fileVersion = VersionUtil.NormalizeVersion(new Version(ver));
							if (isOlder(fileVersion)) {
								Logger.Info("Removing old download: " + fi.Name);
								fi.Delete();
							} else {
								Logger.Debug("Retaining file. {1} is the same or newer than {1}", fi.Name, assemblyVersion);
							}
						} else {
							Logger.Debug("skipping file named {0}", f);
						}
					} else {
						Logger.Debug("file named {0} did not exist?", f);
					}
				}
			} catch (Exception ex) {
				Logger.Error(ex, "Unexpected exception");
			}
		}

		private void StopZiti() {
			Logger.Info("Stopping the ziti service...");
			controller = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == "ziti");
			bool cleanStop = false;
			if (controller != null && controller.Status != ServiceControllerStatus.Stopped) {
				try {
					controller.Stop();
					Logger.Debug("Waiting for the ziti service to stop.");
					controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
					Logger.Debug("The ziti service was stopped successfully.");
					cleanStop = true;
				} catch (Exception e) {
					Logger.Error(e, "Timout while trying to stop service!");
				}
			} else {
				Logger.Debug("The ziti has ALREADY been stopped successfully.");
			}
			if (!cleanStop) {
				Logger.Debug("Stopping ziti-tunnel forcefully.");
				stopProcessForcefully("ziti-tunnel", "data service [ziti]");
			}
		}

		private void stopProcessForcefully(string processName, string description) {
			try {
				Logger.Info("Closing the {description} process", description);
				Process[] workers = Process.GetProcessesByName(processName);
				if (workers.Length < 1) {
					Logger.Info("No {description} process found to close.", description);
					return;
				}
				foreach (Process worker in workers) {
					try {
						Logger.Info("Kiling: {0}", worker);
						if (!worker.CloseMainWindow()) {
							//don't care right now because when called on the UI it just gets 'hidden'
						}
						worker.Kill();
						worker.WaitForExit(5000);
						Logger.Info("Stopping the {description} process exited cleanly");
						worker.Dispose();
					} catch (Exception e) {
						Logger.Error(e, "Unexpected error when closing the {description}!", description);
					}
				}
			} catch (Exception e) {
				Logger.Error(e, "Unexpected error when closing the {description}!", description);
			}
		}

		async private Task StopUI() {
			//first try to ask the UI to exit:

			MonitorServiceStatusEvent status = new MonitorServiceStatusEvent() {
				Code = 0,
				Error = "",
				Message = "Upgrading"
			};
			EventRegistry.SendEventToConsumers(status);

			await Task.Delay(1000); //wait for the event to send and give the UI time to close...

			stopProcessForcefully("ZitiDesktopEdge", "UI");
		}

		private static void Svc_OnShutdownEvent(object sender, StatusEvent e) {
			Logger.Info("the service is shutting down normally...");

			MonitorServiceStatusEvent status = new MonitorServiceStatusEvent() {
				Code = 0,
				Error = "SERVICE DOWN",
				Message = "SERVICE DOWN",
				Status = ServiceActions.ServiceStatus()
			};
			EventRegistry.SendEventToConsumers(status);
		}

		private void Svc_OnTunnelStatusEvent(object sender, TunnelStatusEvent e) {
			string dns = e?.Status?.IpInfo?.DNS;
			string version = e?.Status?.ServiceVersion.Version;
			string op = e?.Op;
			Logger.Info($"Operation {op}. running dns: {dns} at version {version}");

			try {
				dnsIpAddress = IPAddress.Parse(dns);
			} catch {
				//ignore it
			}
		}

		private void Svc_OnClientConnected(object sender, object e) {
			Logger.Info("successfully connected to service");
			if (!dnsProbeTimer.Enabled) {
				dnsProbeTimer.Enabled = true;
				dnsProbeTimer.Start();
				Logger.Info("DNS Probe enabled");
			}
		}

		private void Svc_OnClientDisconnected(object sender, object e) {
			//dnsProbeTimer.Stop();
			DataClient svc = (DataClient)sender;
			if (svc.CleanShutdown) {
				//then this is fine and expected - the service is shutting down
				Logger.Info("client disconnected due to clean service shutdown");

				MonitorServiceStatusEvent status = new MonitorServiceStatusEvent() {
					Code = 0,
					Error = "SERVICE DOWN",
					Message = "SERVICE DOWN",
					Status = ServiceActions.ServiceStatus()
				};
				EventRegistry.SendEventToConsumers(status);
			} else {
				Logger.Error("SERVICE IS DOWN and did not exit cleanly.");

				MonitorServiceStatusEvent status = new MonitorServiceStatusEvent() {
					Code = 10,
					Error = "SERVICE DOWN",
					Message = "SERVICE DOWN",
					Status = ServiceActions.ServiceStatus(),
					ReleaseStream = IsBeta ? "beta" : "stable"
				};
				EventRegistry.SendEventToConsumers(status);
			}
		}

		private static void EnumerateDNS() {
			var ps = System.Management.Automation.PowerShell.Create();
			ps.AddScript("Get-DnsClientServerAddress");
			var results = ps.Invoke();

			using (StringWriter sw = new StringWriter()) {
				foreach (var r in results) {
					string name = (string)r.Properties["InterfaceAlias"].Value;
					string[] dnses = (string[])r.Properties["ServerAddresses"].Value;
					sw.WriteLine($"Interface: {name}. DNS: {string.Join(",", dnses)}");
				}
				Logger.Info("DNS RESULTS:\n{0}", sw.ToString());
			}
		}

		private InstallationNotificationEvent NotifyInstallationUpdates(Checkers.ZDEInstallerInfo info, int code, string error) {
			try {
				InstallationNotificationEvent installationNotificationEvent = new InstallationNotificationEvent() {
					Code = code,
					Error = error,
					Message = "InstallationUpdate",
					Type = "Notification",
					CreationDate = info.CreationTime,
					ZDEVersion = info.Version.ToString(),
					IsCritical = info.IsCritical,
					TimeRemaining = info.TimeRemaining,
					InstallTime = info.InstallTime
				};
				EventRegistry.SendEventToConsumers(installationNotificationEvent);
				Logger.Debug("The installation updates for version {0} is sent to the events pipe...", info.Version);
				return installationNotificationEvent;
			} catch (Exception e) {
				Logger.Error("The notification for the installation updates for version {0} has failed", info.Version);
			}
			return null;

		}
	}
}