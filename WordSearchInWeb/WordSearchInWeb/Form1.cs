using System;
using System.Net;
using System.IO;
using System.Collections.Generic;
using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace WordSearchInWeb
{
    public partial class Form1 : Form
    {
        private Semaphore pool;
        private string rootURL;
        private int TreadCount;
        private int MaxURL;
        private int runningThreads;
        private string SearchingText;
        private ConcurrentQueue<string> URLs;
        private object sync = new object();
        private HashSet<string> uniqueUrls = new HashSet<string>();
        private ManualResetEvent pause = new ManualResetEvent(true);
        private bool IsPaused;
        private CancellationTokenSource cts = new CancellationTokenSource();
        public Form1()
        {
            InitializeComponent();
            listView1.Columns[0].Width = 575;
            listView1.Columns[1].Width = 75;            
        }
        private void AddItem(Entry entry)
        {
            RunOnMainThread(() =>
            {
                ListViewItem item = new ListViewItem();
                item.Text = entry.Url;
                var status = item.SubItems.Add(Translate(entry.Status));
                status.Name = "Status";
                listView1.Items.Add(item);
            });
        }
        private void UpdateItem(Entry entry)
        {
            RunOnMainThread(() =>
            {
                var item = listView1.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == entry.Url);
                if (item != null)
                {
                    item.SubItems["Status"].Text = Translate(entry.Status);
                }
            });
        }
        private void RunOnMainThread(Action action)
        {
            if (InvokeRequired)
            {
                try
                {
                    Invoke(action);
                }
                catch { }
            }
            else
            {
                action();
            }
        }
        private string Translate(Status stat)
        {
            switch (stat)
            {
                case Status.Processing:
                    return "Обработка";
                case Status.Found:
                    return "Найдено";
                case Status.NotFound:
                    return "Не найдено";
                case Status.Error:
                    return "Ошибка";
                default:
                    break;
            } return "";
        }
        void SetState(State state)
        {
            switch (state)
            {
                case State.Processing:
                    button1.Enabled = false;
                    button2.Enabled = true;
                    button3.Enabled = true;
                    textBoxMaxURL.Enabled = false;
                    textBoxSW.Enabled = false;
                    textBoxTreadCount.Enabled = false;
                    textBoxURL.Enabled = false;
                    StatusLabel.Text = "Идет обработка";
                    break;
                case State.Stop:
                    SetPaused(false);
                    button1.Enabled = true;
                    button2.Enabled = false;
                    button3.Enabled = false;
                    textBoxMaxURL.Enabled = true;
                    textBoxSW.Enabled = true;
                    textBoxTreadCount.Enabled = true;
                    textBoxURL.Enabled = true;
                    StatusLabel.Text = "Готов к работе";
                    break;
                case State.Done:
                    button1.Enabled = true;
                    button2.Enabled = false;
                    button3.Enabled = false;
                    textBoxMaxURL.Enabled = true;
                    textBoxSW.Enabled = true;
                    textBoxTreadCount.Enabled = true;
                    textBoxURL.Enabled = true;
                    StatusLabel.Text = "Выполнено";
                    break;
            }
        }
        public void MainFlow()
        {
            uniqueUrls.Clear();
            uniqueUrls.Add(rootURL);
            var token = cts.Token;
            URLs = new ConcurrentQueue<string>();
            URLs.Enqueue(rootURL);

            //семафор
            pool = new Semaphore(TreadCount, TreadCount);
            List<Task> tasks = new List<Task>();
            while ((URLs.Count > 0 || runningThreads > 0) && MaxURL > 0)
            {
                pause.WaitOne();                                    //пауза
                if (token.IsCancellationRequested) { return; }        //стоп
                if (URLs.Count == 0)
                {
                    Thread.Sleep(100);
                    continue;
                }
                string URL;
                if (URLs.TryDequeue(out URL))
                {
                    MaxURL--;
                    Interlocked.Increment(ref runningThreads);
                    Task t = Task.Run(() => ParseHTML(URL), cts.Token);
                    Task continuation = t.ContinueWith(task => Interlocked.Decrement(ref runningThreads), TaskContinuationOptions.ExecuteSynchronously);
                    tasks.Add(continuation);
                }
            }

            Task.WhenAll(tasks.ToArray()).ContinueWith(task =>
            {
                if (token.IsCancellationRequested) { return; }        //стоп
                RunOnMainThread(() =>
                {
                    SetState(State.Done);
                });
            });
        }
        private void ParseHTML(string URL)
        {
            var token = cts.Token;
            pool.WaitOne();
            pause.WaitOne();
            //закачка страницы
            if (token.IsCancellationRequested) { return; }        //стоп
            string text = "";
            WebClient client = new WebClient();
            client.Encoding = System.Text.Encoding.UTF8;
            client.Headers.Add("user-agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)");

            var entry = new Entry() { Url = URL, Status = Status.Processing };
            AddItem(entry);

            try
            {
                text = client.DownloadString(URL);
            }
            catch
            {
                entry.Status = Status.Error;
            }


            if (entry.Status != Status.Error)
            {
                if (token.IsCancellationRequested) { return; }        //стоп
                //поиск ссылок
                string pattern = @"a href=""http://";
                Regex newReg = new Regex(pattern, RegexOptions.IgnoreCase);
                MatchCollection mats = newReg.Matches(text);
                lock (sync)
                {
                    foreach (Match mat in mats)
                    {
                        string link = text.Substring(mat.Index + 8, text.IndexOf('"', mat.Index + 15) - mat.Index - 8);
                        if (uniqueUrls.Add(link))
                        {
                            if (token.IsCancellationRequested) { return; }        //стоп
                            URLs.Enqueue(link);
                        }
                    }
                }

                //поиск текста
                Regex newReg2 = new Regex(SearchingText, RegexOptions.IgnoreCase);
                Match mat2 = newReg2.Match(text);
                entry.Status = Status.NotFound;
                while (mat2.Success)
                {
                    if (token.IsCancellationRequested) { return; }        //стоп
                    //отбрасим все найденные слова между символами "<" и ">"
                    int index1 = text.IndexOf('>', mat2.Index);
                    if (index1 != -1) //если символ ">" найден после найденого слова
                    {
                        int index2 = text.IndexOf('<', mat2.Index);
                        if (index2 != -1) //если символ "<" найден после найденого слова                    
                        {
                            if (index1 > index2)   //искомое слово найдено
                            {
                                entry.Status = Status.Found;
                                break;
                            }
                        }
                    }
                    else
                    {
                        entry.Status = Status.Found;
                        break;
                    }
                    mat2 = mat2.NextMatch();
                }
            }

            UpdateItem(entry);
            pool.Release();
        }
        private void SetPaused(bool paused)
        {
            if (paused)
            {
                pause.Reset(); // pause
                button2.Text = "Возобновить";
                StatusLabel.Text = "Пауза";
            }
            else
            {
                pause.Set(); // resume
                button2.Text = "Пауза";
                StatusLabel.Text = "Идет обработка";
            }

            IsPaused = paused;
        }
        //старт
        private void OnButtonStartClick(object sender, EventArgs e)
        {
            rootURL = textBoxURL.Text;
            TreadCount = (int)textBoxTreadCount.Value;
            MaxURL = (int)textBoxMaxURL.Value;
            toolStripStatusLabel1.Text = "";
            SearchingText = textBoxSW.Text;
            listView1.Items.Clear();
            SetState(State.Processing);
            SetPaused(false);

            Task t = Task.Run(() => MainFlow());
        }
        //пауза
        private void OnButtonPauseClick(object sender, EventArgs e)
        {
            SetPaused(!IsPaused);
        }
        //стоп
        private void OnButtonStopClick(object sender, EventArgs e)
        {
            cts.Cancel();
            SetState(State.Stop);
        }

        protected override void OnClosed(EventArgs e)
        {
            cts.Cancel();
            base.OnClosed(e);
        }
    }
}
