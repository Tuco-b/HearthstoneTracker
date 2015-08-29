namespace HearthCap.StartUp
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.Composition;
    using System.ComponentModel.Composition.Hosting;
    using System.ComponentModel.Composition.Primitives;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Markup;

    using Caliburn.Micro;
    using Caliburn.Micro.Recipes.Filters.Framework;

    using HearthCap.Core.GameCapture;
    using HearthCap.Data;
    using HearthCap.Logging;
    using HearthCap.Shell;
    using HearthCap.Shell.Dialogs;

    using Configuration = HearthCap.Data.Migrations.Configuration;

    public sealed class AppBootstrapper : BootstrapperBase, IDisposable
    {
        private static CompositionContainer container;

        private IAppLogManager _logManager;

        public AppBootstrapper()
        {
            Initialize();
        }

        public static CompositionContainer Container
        {
            get
            {
                return container;
            }
        }

        public void Dispose()
        {
            if (Container != null)
            {
                Container.Dispose();
            }

            if (_logManager != null)
            {
                _logManager.Flush();
                _logManager.Dispose();
            }
        }

        protected override void Configure()
        {
            InitializeApplicationDataDirectory();

            var composeTask = Task.Factory.StartNew(() =>
                {
                    // var aggregateCatalog = new AggregateCatalog(AssemblySource.Instance.Select(x => new AssemblyCatalog(x)));
                    var aggregateCatalog = new AggregateCatalog(new ComposablePartCatalog[]
                                                                    {
                                                                new AssemblyCatalog(GetType().Assembly),
                                                                new AssemblyCatalog(typeof(AutoCaptureEngine).Assembly),
                                                                new AssemblyCatalog(typeof(IRepository<>).Assembly),
                                                                    });
                    container = new CompositionContainer(aggregateCatalog);
                    var batch = new CompositionBatch();
                    batch.AddExportedValue(container);
                    batch.AddExportedValue<IEventAggregator>(new EventAggregator());
                    batch.AddExportedValue<IWindowManager>(new CustomWindowManager());
                    batch.AddExportedValue<Func<IMessageBox>>(() => container.GetExportedValue<IMessageBox>());
                    batch.AddExportedValue<Func<HearthStatsDbContext>>(() => new HearthStatsDbContext());

                    // batch.AddExportedValue<IWindowManager>(new AppWindowManager());
                    // batch.AddExportedValue(MessageBus.Current);

                    var compose = container.GetExportedValue<CompositionBuilder>();
                    compose.Compose(batch);
                    // var composeTasks = this.GetAllInstances<ICompositionTask>();
                    // composeTasks.Apply(s => s.Compose(batch));
                    container.Compose(batch);
                });

            var initDbTask = Task.Factory.StartNew(InitializeDatabase);

            Task.WaitAll(composeTask, initDbTask);

            var logPath = Path.Combine((string)AppDomain.CurrentDomain.GetData("DataDirectory"), "logs");
            _logManager = container.GetExportedValue<IAppLogManager>();
            _logManager.Initialize(logPath);
            container.GetExportedValue<CrashManager>().WireUp();

            // Apply xaml/wpf fixes
            var currentUICult = Thread.CurrentThread.CurrentUICulture.Name;
            var currentCult = Thread.CurrentThread.CurrentCulture.Name;

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(currentUICult);
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(currentCult);

            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

            // Hook caliburn filter
            FilterFrameworkCoreCustomization.Hook();

            // Hook application events
            Application.Activated += (s, e) => container.GetExportedValue<IEventAggregator>().PublishOnCurrentThread(new ApplicationActivatedEvent());
            Application.Deactivated += (s, e) => container.GetExportedValue<IEventAggregator>().PublishOnCurrentThread(new ApplicationDeActivatedEvent());
        }

        private void InitializeApplicationDataDirectory()
        {
            string appFolderName;
            using (var reg = new DataDirectorySettings())
            {
                appFolderName = reg.DataDirectory;
                if (String.IsNullOrEmpty(reg.DataDirectory) || !Directory.Exists(reg.DataDirectory))
                {
                    appFolderName = "HearthstoneTracker";
                    appFolderName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appFolderName);
                    if (!Directory.Exists(appFolderName))
                    {
                        Directory.CreateDirectory(appFolderName);
                    }
                    reg.DataDirectory = appFolderName;
                }
            }

            var logFolder = Path.Combine(appFolderName, "logs");

            if (!Directory.Exists(logFolder))
            {
                Directory.CreateDirectory(logFolder);
            }

            AppDomain.CurrentDomain.SetData("DataDirectory", appFolderName);
        }

        protected override IEnumerable<object> GetAllInstances(Type serviceType)
        {
            return container.GetExportedValues<object>(AttributedModelServices.GetContractName(serviceType));
        }

        protected override object GetInstance(Type serviceType, string key)
        {
            var str = String.IsNullOrEmpty(key) ? AttributedModelServices.GetContractName(serviceType) : key;
            var obj = container.GetExportedValues<object>(str).FirstOrDefault<object>();
            if (obj == null)
            {
                CultureInfo invariantCulture = CultureInfo.InvariantCulture;
                var objArray = new object[] { str };
                throw new InvalidOperationException(string.Format(invariantCulture, "Could not locate any exported values for '{0}'.", objArray));
            }

            return obj;
        }

        protected override void BuildUp(object instance)
        {
            container.SatisfyImportsOnce(instance);
        }

        protected override void OnStartup(object sender, StartupEventArgs e)
        {
            var startupTasks = GetAllInstances(typeof(IStartupTask)).Cast<IStartupTask>();
            startupTasks.Apply(s => s.Run());

            // var userSettings = (UserPreferences)GetInstance(typeof(UserPreferences), null);
            // var trayIcon = (TrayIconViewModel)GetInstance(typeof(TrayIconViewModel), null);
            var windowManager = (CustomWindowManager)GetInstance(typeof(CustomWindowManager), null);
            var start = windowManager.MainWindow<StartupViewModel>();
            start.Show();
            start.Hide();
            // DisplayRootViewFor<StartupViewModel>();
        }

        private void InitializeDatabase()
        {
            // Only seed when needed
            var dataDir = (string)AppDomain.CurrentDomain.GetData("DataDirectory");
            var seedFile = Path.Combine(dataDir, "db.seed");
            int currentSeed = 0;
            if (File.Exists(seedFile))
            {
                int.TryParse(File.ReadAllText(seedFile), out currentSeed);
            }
            if (currentSeed < Configuration.SeedVersion)
            {
                using (var context = new HearthStatsDbContext())
                {
                    new DbInitializer().InitializeDatabase(context);
                }

                File.WriteAllText(seedFile, Configuration.SeedVersion.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                using (var context = new HearthStatsDbContext())
                {
                    var settings = context.Settings.FirstOrDefault();
                }
            }
        }
    }

    [Export(typeof(CompositionBuilder))]
    public class CompositionBuilder
    {
        private readonly IEnumerable<ICompositionTask> _tasks;

        [ImportingConstructor]
        public CompositionBuilder([ImportMany]IEnumerable<ICompositionTask> tasks)
        {
            _tasks = tasks;
        }

        public void Compose(CompositionBatch batch)
        {
            _tasks.Apply(s => s.Compose(batch));
        }
    }
}