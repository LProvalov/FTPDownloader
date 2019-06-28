﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using DBDownloader.MainLogger;
using DBDownloader.ConfigReader;
using DBDownloader.Providers;
using DBDownloader.Net;
using DBDownloader.Net.FTP;
using DBDownloader.Net.HTTP;

namespace DBDownloader.Engine
{
    public class LoadingManager
    {
        private Dictionary<string, DownloadFile> downloadFileDictionary =
            new Dictionary<string, DownloadFile>();

        private bool isLoading = false;
        private Uri reportDirUrl;
        private DataProvider dataProvider;

        private bool isLoadedEnd = false;
        public bool IsLoadedEnd { get { return isLoadedEnd; } }

        private EventHandler cancelEventHandler;
        public event EventHandler DownloadingStopped;
        public event ErrorEventHandler ErrorOccurred;
        
        public LoadingManager(Uri reportDirUrl)
        {
            this.reportDirUrl = reportDirUrl;
            dataProvider = new DataProvider();
        }
               
        public bool IsLoading { get { return isLoading; } }

        public IEnumerable<FileStatus> GetStatuses()
        {
            List<FileStatus> response = new List<FileStatus>();
            try
            {
                foreach (DownloadFile df in downloadFileDictionary.Values)
                {
                    FileStatus fs = new FileStatus();
                    fs.Title = df.Title;
                    fs.FileName = df.DestinationFile.Name;
                    switch (df.GetDownloaderStatus())
                    {
                        case NetFileDownloader.NetDownloaderStatus.inprogress:
                            fs.Status = "In progress...";
                            break;
                        case NetFileDownloader.NetDownloaderStatus.stopping:
                            fs.Status = "Stopping...";
                            break;
                        case NetFileDownloader.NetDownloaderStatus.stopped:
                            fs.Status = "Stopped";
                            break;
                        case NetFileDownloader.NetDownloaderStatus.erroroccured:
                            fs.Status = "Error Occured";
                            break;
                        case NetFileDownloader.NetDownloaderStatus.weberroroccured:
                            fs.Status = "Web Error Occured";
                            break;
                    }
                    fs.PercentOfComplete = df.GetPercentOfComplete();
                    fs.DestFileSize = df.DestinationFileDownloadedLength;

                    fs.SourceFileSize = df.GetBytesOfFileThatNeedToBeDownloaded();
                    fs.IsUpdateNeeded = df.IsUpdateNeeded;
                    fs.IsErrorOccured = df.IsErrorOccured;
                    fs.ErrorMessage = df.ErrorMessage;
                    response.Add(fs);
                }
            }
            catch (InvalidOperationException ioEx)
            {

            }
            return response;
        }

        public void AddFileToDownload(string title, string fileName,
            FileInfo destinationFile, string sourceFileUrl, DateTime creationFileDateTime,
            bool isUpdateNeeded, long sourceSize = 0)
        {
            // TODO: store title in different place for example create special object 
            // what be include DownloadingItem Id, FileStatus object and some additional information.
            if (!isLoading)
            {
                Configuration configuration = Configuration.Instance;
                FileInfo destinationFileInfo = destinationFile;
                Uri sourceFileUri = new Uri(sourceFileUrl);
                INetClient netClient = null;
                if (configuration.NetClientType == NetFileDownloader.NetClientTypes.FTP) {
                    netClient = FtpClient.CreateClient();
                } else
                {
                    netClient = HttpClient.CreateClient();
                }
                DownloadFile df = new DownloadFile(
                    netClient,
                    destinationFileInfo, sourceFileUri,
                    configuration.UseProxy, configuration.ProxyAddress, configuration.UsePassiveFTP,
                    creationFileDateTime,
                    isUpdateNeeded, sourceSize);
                df.errorEvent += ThrowError;
                df.Title = title;
                df.RepeatCount = configuration.CountOfRepeat;
                df.DelayTime = configuration.RepeatDalay;
                if (!downloadFileDictionary.ContainsKey(fileName))
                    downloadFileDictionary.Add(fileName, df);
            }
        }
        
        public Task BeginAsync()
        {
            Log.WriteInfo("LoadingManager BeginAsync");
            if (!isLoading)
            {
                isLoading = true;
                return Task.Factory.StartNew(() =>
                {
                    Log.WriteInfo("LoadingManager loading started");

                    foreach (KeyValuePair<string, DownloadFile> dfItem in downloadFileDictionary)
                    {
                        CancellationTokenSource cts = new CancellationTokenSource();
                        cancelEventHandler = (obj, args) => { cts.Cancel(); };
                        DownloadFile df = dfItem.Value;
                        try
                        {
                            if (df.IsUpdateNeeded)
                            {
                                Log.WriteTrace("{0} begin loading", df.SourceFileUri.AbsoluteUri);
                                df.BeginAsync().Wait(cts.Token);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            df.CancelDownloading();
                            isLoadedEnd = false;
                            break;
                        }
                        finally
                        {
                            if (cts.IsCancellationRequested) isLoadedEnd = false;
                            else isLoadedEnd = true;
                            cts.Dispose();
                        }
                    }
                    isLoading = false;
                    if (DownloadingStopped != null) DownloadingStopped.Invoke(this, new EventArgs());
                });
            }
            return null;
        }
        public void Cancel()
        {
            isLoadedEnd = false;
            
            if (cancelEventHandler != null) cancelEventHandler.Invoke(this, new EventArgs());
        }

        public struct FileStatus
        {
            public string Title;
            public string FileName;
            public string Status;
            public int PercentOfComplete;
            public long DestFileSize;
            public long SourceFileSize;
            public bool IsUpdateNeeded;
            public bool IsErrorOccured;
            public string ErrorMessage;
        }

        public struct FtpFileInfo
        {
            public string FileName;
            public long FileSize;
            public DateTime CreatedDate;
            public DateTime UpdatedDate;
        }

        private void ThrowError(object sender, ErrorEventArgs args)
        {
            if (ErrorOccurred != null) ErrorOccurred.BeginInvoke(this, args, null, null);
        }

        public int DownloadingFileLenght
        {
            get { return downloadFileDictionary.Count; }
        }
    }
}
