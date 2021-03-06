﻿using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using DBDownloader.MainLogger;

namespace DBDownloader.FTP
{
    public class FTPDownloader
    {
        public enum FTPDownloaderStatus
        {
            stopped,
            stopping,
            inprogress,
            erroroccured,
            weberroroccured
        }

        private const int BUFFER_SIZE = 1024;

        private Uri sourceUri;
        private NetworkCredential credential;
        private FileInfo destinationFileInfo;
        private bool isDownloadStarted;
        private bool isDownloadFinished = false;
        private long bytesDownloaded;
        private long bytesDownloadingSize;
        private EventHandler cancelEventHandler;
        private CancellationTokenSource cancellationToken;
        private CancellationTokenSource loopCancellationTokenSource = null;
        private string errorMessage = string.Empty;
        private bool isErrorOccured = false;
        private FTPDownloaderStatus _status;
        private FtpStatusCode _ftpStatusCode;
        private bool deleteDestinationFile = false;

        private int delayTime = 10000;
        public int DelayTime
        {
            get { return delayTime; }
            set { delayTime = value; }
        }
        private int repeatCount = 10;
        public int RepeatCount
        {
            get { return repeatCount; }
            set { repeatCount = value; }
        }

        public event ErrorEventHandler ErrorOccuredEvent;

        public FTPDownloaderStatus Status { get { return _status; } }
        public bool UseProxy { get; set; } = false;
        public string ProxyAddress { get; set; } = string.Empty;
        public bool UsePassiveFTP { get; set; } = true;
        public bool IsErrorOccured { get { return isErrorOccured; } }
        public string ErrorMessage { get { return errorMessage; } }
        public long BytesSourceFileSize { get { return bytesDownloadingSize; } }
        public int PercentOfComplete
        {
            get
            {
                long already = 0;
                if (destinationFileInfo != null)
                {
                    destinationFileInfo.Refresh();
                    already = destinationFileInfo.Exists ? destinationFileInfo.Length : 0;
                }
                return bytesDownloadingSize != 0 ?
                    (int)((double)(bytesDownloaded + already) / bytesDownloadingSize * 100) : 0;
            }
        }

        public class DateTimeEventArgs : EventArgs
        {
            private DateTime _creationDateTime;
            public DateTimeEventArgs(DateTime creationDateTime)
            {
                this._creationDateTime = creationDateTime;
            }
            public DateTime CreationFileDateTime { get { return _creationDateTime; } }
        }

        public EventHandler DownloadEndEvent;

        public FTPDownloader(string username, string password, string destinationFileName, string sourceUri, long sourceSize = 0)
            : this(new NetworkCredential(username, password), new FileInfo(destinationFileName), new Uri(sourceUri), sourceSize)
        {
        }

        public FTPDownloader(NetworkCredential networkCredential,
            FileInfo destinationFileInfo, Uri sourceUri,
            long sourceSize = 0)
        {
            this.sourceUri = sourceUri;
            this.destinationFileInfo = destinationFileInfo;
            this.bytesDownloadingSize = sourceSize;
            credential = networkCredential;
            isDownloadStarted = false;
            cancelEventHandler += (sender, obj) =>
            {
                if (cancellationToken != null) cancellationToken.Cancel();
            };
        }

        private FtpWebRequest CreateFtpRequest(string method)
        {
            FtpWebRequest request = WebRequest.Create(sourceUri) as FtpWebRequest;
            request.Credentials = credential;
            if (UseProxy) request.Proxy = (string.IsNullOrEmpty(ProxyAddress)) ?
                     new WebProxy() : new WebProxy(ProxyAddress);
            request.UsePassive = UsePassiveFTP;
            request.Method = method;
            return request;
        }

        public Task BeginAsync()
        {
            if (destinationFileInfo != null && credential != null)
            {
                return ResumeFtpFileDownloadAsync(sourceUri, destinationFileInfo);
            }
            throw new ArgumentException("destinationFileInfo or credential cant be null");
        }

        public void Cancel()
        {
            _status = FTPDownloaderStatus.stopping;
            if (loopCancellationTokenSource != null)
                loopCancellationTokenSource.Cancel();
            if (cancelEventHandler != null)
                cancelEventHandler.Invoke(this, new EventArgs());
        }

        public long GetSourceFileLength()
        {
            Log.WriteTrace("FTPDownloader - GetSourceFileLength");
            FtpWebRequest request = CreateFtpRequest(WebRequestMethods.Ftp.GetFileSize);
            request.UseBinary = false;
            try
            {
                FtpWebResponse response = (FtpWebResponse)request.GetResponse();
                bytesDownloadingSize = response.ContentLength;
                Log.WriteTrace("FTPDownloader - GetSourceFileLength : {0} bytes", bytesDownloadingSize);
                response.Close();
            }
            catch (WebException wEx)
            {
                String status = ((FtpWebResponse)wEx.Response).StatusDescription;
                Log.WriteTrace("FTPDownloader - GetSourceFileLength Error: {0}", status);
                if (bytesDownloadingSize < 0) bytesDownloadingSize = 0;
            }
            return bytesDownloadingSize;
        }

        private void ResumeFtpFileDownload(Uri sourceUri, FileInfo destinationFile)
        {
            _status = FTPDownloaderStatus.inprogress;
            Log.WriteTrace("FTPDownloader - status inprogress");
            using (cancellationToken = new CancellationTokenSource())
            {
                isDownloadStarted = true;
                FileStream localfileStream = null;
                FtpWebRequest request = CreateFtpRequest(WebRequestMethods.Ftp.DownloadFile);
                request.UseBinary = true;

                WebResponse response = null;
                Stream responseStream = null;
                try
                {
                    if (destinationFile.Exists)
                    {
                        request.ContentOffset = destinationFile.Length;
                        Log.WriteTrace("Try to open local file: {0} for append, start from: {1}", destinationFile.FullName, destinationFile.Length);
                        localfileStream = new FileStream(destinationFile.FullName,
                            FileMode.Append, FileAccess.Write);
                    }
                    else
                    {
                        Log.WriteTrace("Try to create local file: {0}", destinationFile.FullName);
                        localfileStream = new FileStream(destinationFile.FullName,
                            FileMode.Create, FileAccess.Write);
                    }
                    Log.WriteTrace("File opened for writing");
                    response = request.GetResponse();
                    using (responseStream = response.GetResponseStream())
                    {
                        byte[] buffer = new byte[BUFFER_SIZE];
                        int bytesRead = responseStream.Read(buffer, 0, BUFFER_SIZE);

                        while (bytesRead != 0 && !cancellationToken.IsCancellationRequested)
                        {
                            localfileStream.Write(buffer, 0, bytesRead);
                            bytesDownloaded += bytesRead;
                            bytesRead = responseStream.Read(buffer, 0, BUFFER_SIZE);
                        }
                    }
                }
                catch (WebException wEx)
                {
                    String errorStatusDescription = ((FtpWebResponse)wEx.Response).StatusDescription;
                    _ftpStatusCode = ((FtpWebResponse)wEx.Response).StatusCode;
                    String errorStatus = ((FtpWebResponse)wEx.Response).StatusCode.ToString();
                    Log.WriteError("FTPDownloader - web error status {0}:{1}\nWeb error occurred:{2}", errorStatus, errorStatusDescription, wEx.Message);
                    if (errorStatus == "554") deleteDestinationFile = true;

                    ReportWriter.AppendString("Загрузка файла {0} - FAILED : {1}\n", sourceUri, wEx.Message);
                    if (wEx.InnerException != null)
                    {
                        Log.WriteTrace("FTPDownloader - inner Exception:{0}", wEx.InnerException.Message);
                        ReportWriter.AppendString("Inner Exception: {0}\n", wEx.InnerException.Message);
                    }

                    _status = FTPDownloaderStatus.weberroroccured;
                    isErrorOccured = true;
                    errorMessage = wEx.Message;
                    ErrorOccuredEvent.BeginInvoke(this, new ErrorEventArgs(wEx), null, null);
                }
                catch (Exception ex)
                {
                    Log.WriteError("FTPDownloader - error occurred:{0}", ex.Message);
                    if (ex.InnerException != null)
                        Log.WriteTrace("FTPDownloader - inner Exception:{0}", ex.InnerException.Message);
                    _status = FTPDownloaderStatus.erroroccured;
                    isErrorOccured = true;
                    errorMessage = ex.Message;
                    ErrorOccuredEvent.BeginInvoke(this, new ErrorEventArgs(ex), null, null);
                }
                finally
                {
                    try
                    {
                        if (response != null)
                        {
                            Log.WriteTrace("FTPDownloader - Closing response");
                            response.Close();
                        }
                        if (responseStream != null)
                        {
                            Log.WriteTrace("FTPDownloader - Closing resposeStream");
                            responseStream.Close();
                        }
                        if (localfileStream != null)
                        {
                            Log.WriteTrace("FTPDownloader - Closing localFileStream");
                            localfileStream.Close();
                        }

                        isDownloadStarted = false;
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            isDownloadFinished = true;
                            if (DownloadEndEvent != null) DownloadEndEvent.Invoke(this, new EventArgs());
                            ReportWriter.AppendString("Загрузка файла {0} - ОК\n", sourceUri);
                        }
                        destinationFile.Refresh();
                        Log.WriteTrace("FTPDownloader Destination file ({1}) length: {0}", destinationFile.Length, destinationFile.Name);
                        if (destinationFile.Exists && (destinationFile.Length == 0 || deleteDestinationFile))
                        {
                            File.Delete(destinationFile.FullName);
                        }
                    }
                    catch { }

                    if (_status != FTPDownloaderStatus.erroroccured && _status != FTPDownloaderStatus.weberroroccured)
                        _status = FTPDownloaderStatus.stopped;
                    Log.WriteTrace("FTPDownloader - stop downloading, status:{0}", _status);
                }
            }
            cancellationToken = null;
        }

        private Task ResumeFtpFileDownloadAsync(Uri sourceUri, FileInfo destinationFile)
        {
            return Task.Factory.StartNew(() =>
            {
                Log.WriteTrace("Start downloading: {0}", sourceUri.AbsoluteUri);
                GetSourceFileLength();
                int loopCount = repeatCount;
                do
                {
                    ResumeFtpFileDownload(sourceUri, destinationFile);
                    Log.WriteTrace("FTPDownloader status: {0}", _status);
                    if (_status == FTPDownloaderStatus.weberroroccured)
                    {
                        Log.WriteTrace("FTPDownloader WebError Occurred, wait {0}ms and repeat {1}", delayTime, loopCount);
                        using (loopCancellationTokenSource = new CancellationTokenSource())
                        {
                            loopCancellationTokenSource.Token.WaitHandle.WaitOne(delayTime);
                        }
                        loopCancellationTokenSource = null;
                        if (_ftpStatusCode != FtpStatusCode.ActionNotTakenFileUnavailable &&
                        _ftpStatusCode != FtpStatusCode.ActionNotTakenFileUnavailableOrBusy &&
                        _ftpStatusCode != FtpStatusCode.ActionNotTakenFilenameNotAllowed &&
                        _ftpStatusCode != FtpStatusCode.FileCommandPending)
                            loopCount--;
                    }
                } while (_status == FTPDownloaderStatus.weberroroccured && loopCount > 0);
                _status = FTPDownloaderStatus.stopped;
            });
        }
    }
}
