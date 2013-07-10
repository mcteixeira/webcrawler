using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Threading;
using System.ComponentModel;
using System.Collections.Concurrent;
using GalaSoft.MvvmLight.Command;

namespace WebCrawlerProject
{
    public class ViewModel : INotifyPropertyChanged
    {
        //
        public class ImageData
        {
            public string Url { get; set; }
            public byte[] Data { get; set; }
        }
        public class SiteData
        {
            public int Level { get; set; }
            public string Url { get; set; }
        }

        #region Variaveis e Propriedades
        public int[] DeepnessLevels { get { return new int[] { 1, 2, 3, 4 }; } }
        private int threadCounter = 0;
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private readonly Object lockerVisited = new Object();
        private readonly Object lockerImages = new Object();
        private readonly Object lockerCounter = new Object();

        private List<Task> tasks = new List<Task>();
        CancellationTokenSource tokenSource = new CancellationTokenSource();
        private ConcurrentBag<string> visited = new ConcurrentBag<string>();
        private BlockingCollection<SiteData> urlQueue = new BlockingCollection<SiteData>();
        private BlockingCollection<KeyValuePair<UriType, Uri>> imageQueue = new BlockingCollection<KeyValuePair<UriType, Uri>>();

        private ObservableCollection<ImageData> images = new ObservableCollection<ImageData>();
        public ObservableCollection<ImageData> Images
        {
            get { return images; }
            set { images = value; }
        }

        private string filePath;
        public string FilePath
        {
            get { return filePath; }
            set { filePath = value; RaisePropertyChanged("FilePath"); }
        }

        private List<SiteData> websites;
        private List<SiteData> Websites
        {
            get { return websites; }
            set { websites = value; }
        }

        private int maxThreads = 10;
        public int MaxThreads
        {
            get { return maxThreads; }
            set { maxThreads = value; RaisePropertyChanged("MaxThreads"); }
        }

        private int deepness = 2;
        public int Deepness
        {
            get { return deepness; }
            set
            {
                deepness = value;
                RaisePropertyChanged("Deepness");
            }
        }

        private ImageData selectedItem;
        public ImageData SelectedItem
        {
            get { return selectedItem; }
            set { selectedItem = value; RaisePropertyChanged("SelectedItem"); }
        }

        private bool controlsEnabled = true;
        public bool ControlsEnabled
        {
            get { return controlsEnabled; }
            set { controlsEnabled = value; RaisePropertyChanged("ControlsEnabled"); }
        }

        private bool running = false;
        private bool Running
        {
            get { return running; }
            set
            {
                running = value;
                RaisePropertyChanged("Running");
                UpdateCommands();
            }
        }

        private bool stopingCrawling = false;
        private bool StopingCrawling
        {
            get { return stopingCrawling; }
            set
            {
                stopingCrawling = value;
                RaisePropertyChanged("StopingCrawling");
                UpdateCommands();
            }
        }

        private string stopText = "Stop Crawling";
        public string StopText
        {
            get { return stopText; }
            set
            {
                stopText = value;
                RaisePropertyChanged("StopText");
                UpdateCommands();
            }
        }

        #endregion

        #region Metodos

        //Abre e faz parse do ficheiro txt
        public void OpenFile()
        {
            OpenFileDialog fd = new OpenFileDialog();
            fd.Filter = "Text Files (.txt)|*.txt";
            bool? ok = fd.ShowDialog();

            if (ok == true && File.Exists(fd.FileName))
            {
                try
                {
                    FilePath = fd.FileName;
                    Task.Factory.StartNew(() => File.ReadAllLines(fd.FileName)
                                                    .Where(x => Uri.IsWellFormedUriString(x, UriKind.Absolute))
                                                    .Distinct()
                                                    .Select(x => new SiteData() { Level = 0, Url = x })
                                                    .ToList()
                    ).ContinueWith(res => { Websites = res.Result; });
                }
                catch (IOException)
                {

                }
            }
        }

        //Arranca o processo de Crawling
        public void DoIt()
        {
            if (Websites.Count == 0)
                return;

            Reset();
            
            foreach (SiteData item in Websites)
                urlQueue.Add(item);

            for (int i = 0; i < MaxThreads; i++)
            {
                tasks.Add(Task.Factory.StartNew(() => Crawl(), tokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current));
                Thread.Sleep(5);
            }
            for (int i = 0; i < 5; i++)
               tasks.Add(Task.Factory.StartNew(() => ConsumeImages(), tokenSource.Token,TaskCreationOptions.LongRunning, TaskScheduler.Current));
            tasks.Add(Task.Factory.StartNew(() => FinishMonitor(), tokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current));
        }

        //Função de Crawl (Produtor)
        private void Crawl()
        {
            SiteData s;
            while (urlQueue.TryTake(out s, 15000))
            {
                if (tokenSource.IsCancellationRequested)
                    break;
                Parse(s);
            }
            lock (lockerCounter)
                threadCounter++;
        }

        //Carrega as imagens para o UI (consumidor)
        private void ConsumeImages()
        {
            foreach (var item in imageQueue.GetConsumingEnumerable())
            {
                if (imageQueue.IsCompleted || tokenSource.IsCancellationRequested)
                    return;
                else
                    LoadImage(item);
            }
        }

        //Faz o parse do site (Enqueue de novos sites e das imagens)
        private void Parse(SiteData site)
        {
            SiteData s = site;
            
            if (s.Level < Deepness)
            {
                int level = site.Level + 1;
                WebCrawler wc = new WebCrawler();
                var children = wc.Parse(s.Url).ToList();

                foreach(var node in children)
                {
                    if (tokenSource.IsCancellationRequested)
                        return;

                    if (node.Key == UriType.WebPage)
                        if (!visited.Contains(node.Value.OriginalString))
                            urlQueue.Add(new SiteData() { Level = level, Url = node.Value.OriginalString });

                    if (node.Key == UriType.Image)
                        if (!visited.Contains(node.Value.OriginalString))
                            imageQueue.Add(node);

                    visited.Add(node.Value.OriginalString);
                }
            }
        }

        //Carrega Imagem para UI
        private void LoadImage(KeyValuePair<UriType, Uri> i)
        {
            WebCrawler wc = new WebCrawler();
            var image = wc.LoadImage(i.Value);

            lock (lockerImages)
            {
                dispatcher.InvokeAsync(new Action(() =>
                {
                    if (!ImageExists(image) && image != null)
                        Images.Add(new ImageData() { Url = i.Value.OriginalString, Data = image, });
                }), DispatcherPriority.Background);
            }
        }

        //Monitoriza se já não há mais sites a adicionar
        private void FinishMonitor()
        {
            while (true)
            {
                if (tokenSource.IsCancellationRequested)
                    return;
                
                lock (lockerCounter)
                    if (threadCounter == MaxThreads)
                    {
                        imageQueue.CompleteAdding();
                        dispatcher.InvokeAsync(new Action(() => { Running = false; StopingCrawling = false; ControlsEnabled = true; }), DispatcherPriority.Send);
                        return;
                    }
            }
        }

        //Cancela o Crawl
        public void Shutdown()
        {
            Task.Factory.StartNew(() =>
            {
                
                dispatcher.InvokeAsync(new Action(() => { StopText = "Stopping... Hold on..."; StopingCrawling = true; }), DispatcherPriority.Send);
                tokenSource.Cancel();
                while (tasks.Any(t => t.Status == TaskStatus.Running)) { }

            }).ContinueWith(task => {
                dispatcher.InvokeAsync(new Action(() => { StopText = "Stop Crawl"; Running = false; StopingCrawling = false; ControlsEnabled = true; }), DispatcherPriority.Send);
            });
        }

        //Valida se a imagem já existe
        private bool ImageExists(byte[] image)
        {
            lock (lockerImages)
            {
                if (image != null)
                    return Images.Any(img => img.Data.SequenceEqual(image));
                else
                    return false;
            }
        }

        //Recarrega a imagem clicada com priotridade alta
        private void ReloadLink(SiteData site)
        {
            WebCrawler wc = new WebCrawler();
            var image = wc.LoadImage(new Uri(site.Url));
            lock (lockerImages)
            {
                dispatcher.InvokeAsync(new Action(() =>
                {
                    if (!ImageExists(image))
                        SelectedItem = new ImageData() { Url = site.Url, Data = image, };
                }), DispatcherPriority.Send);
            }
        }

        //Faz reset às variáveis
        private void Reset()
        {
            Images.Clear();
            threadCounter = 0;
            Running = true;
            ControlsEnabled = false;

            tasks = new List<Task>();
            tokenSource = new CancellationTokenSource();
            visited = new ConcurrentBag<string>();
            urlQueue = new BlockingCollection<SiteData>();
            imageQueue = new BlockingCollection<KeyValuePair<UriType, Uri>>();
        }

        #endregion

        #region Interfaces

        //Implementação do interface INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region ICommand

        private RelayCommand openFileCommand;
        public RelayCommand OpenFileCommand
        {
            get
            {
                if (openFileCommand == null)
                {
                    openFileCommand = new RelayCommand(
                            () => this.OpenFile(),
                            () => this.CanOpenFile()
                        );
                }
                return openFileCommand;
            }
        }
        private bool CanOpenFile()
        {
            return (Running == false) ? true : false;
        }

        private RelayCommand runCommand;
        public RelayCommand RunCommand
        {
            get
            {
                if (runCommand == null)
                    runCommand = new RelayCommand(() => this.DoIt(), () => this.CanRun());
                return runCommand;
            }
        }
        private bool CanRun()
        {
            return (Running == false && FilePath != null) ? true : false;
        }

        private RelayCommand stopCommand;
        public RelayCommand StopCommand
        {
            get
            {
                if (stopCommand == null)
                {
                    stopCommand = new RelayCommand(
                        () => this.Shutdown(),
                        () => this.CanStop()
                    );
                }
                return stopCommand;
            }
        }
        private bool CanStop()
        {
            return (Running == true && StopingCrawling == false) ? true : false;
        }

        public void UpdateCommands()
        {
            OpenFileCommand.RaiseCanExecuteChanged();
            RunCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();

        }

        #endregion
    }
}
