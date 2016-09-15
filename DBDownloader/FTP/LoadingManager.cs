﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using DBDownloader.MainLogger;
using DBDownloader.ConfigReader;

namespace DBDownloader.FTP
{
    public class LoadingManager
    {
        private NetworkCredential networkCredential;
        private Dictionary<string, DownloadFile> downloadFileDictionary =
            new Dictionary<string, DownloadFile>();

        private bool isLoading = false;
        private bool useProxy = false;
        private string proxyAddress = string.Empty;
        private Uri reportDirUrl;
        private FTPWorker ftpWorker;
        private Configuration configuration;

        private bool isLoadedEnd = false;
        public bool IsLoadedEnd { get { return isLoadedEnd; } }

        private EventHandler cancelEventHandler;
        public event EventHandler DownloadingStopped;
        public event ErrorEventHandler ErrorOccurred;
        
        public LoadingManager(NetworkCredential networkCredential, Uri reportDirUrl, Configuration configuration)
        {
            this.networkCredential = networkCredential;
            this.reportDirUrl = reportDirUrl;
            this.configuration = configuration;
            ftpWorker = new FTPWorker(networkCredential, useProxy, proxyAddress, configuration.UsePassiveFTP);
        }

        public bool UseProxy
        {
            get { return useProxy; }
            set { useProxy = value; }
        }
        public string ProxyAddress
        {
            get { return proxyAddress; }
            set { proxyAddress = value; }
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
                    switch (df.Downloader.Status)
                    {
                        case FTPDownloader.FTPDownloaderStatus.inprogress:
                            fs.Status = "In progress...";
                            break;
                        case FTPDownloader.FTPDownloaderStatus.stopping:
                            fs.Status = "Stopping...";
                            break;
                        case FTPDownloader.FTPDownloaderStatus.stopped:
                            fs.Status = "Stopped";
                            break;
                        case FTPDownloader.FTPDownloaderStatus.erroroccured:
                            fs.Status = "Error Occured";
                            break;
                        case FTPDownloader.FTPDownloaderStatus.weberroroccured:
                            fs.Status = "Web Error Occured";
                            break;
                    }
                    fs.PercentOfComplete = df.Downloader.PercentOfComplete;
                    fs.DestFileSize = df.DestinationFileDownloadedLength;

                    fs.SourceFileSize = df.Downloader.BytesSourceFileSize;
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
                FileInfo destinationFileInfo = destinationFile;
                Uri sourceFileUri = new Uri(sourceFileUrl);
                DownloadFile df = new DownloadFile(networkCredential,
                    destinationFileInfo, sourceFileUri,
                    useProxy, proxyAddress, configuration.UsePassiveFTP,
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
                            df.Downloader.Cancel();
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
