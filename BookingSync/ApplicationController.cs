﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Data;
using System.Data.SqlClient;
using System.Xml;
using System.Net;
using System.IO;
using Newtonsoft.Json.Linq;

namespace BookingSync
{
    public class ApplicationController
    {
        private int _lastSeatSyncCount = 0;
        private Thread _seatSyncThread = null;
        private bool _isSeatSyncThreadRunning = false;
        private Thread _scheduleSyncThread = null;
        private bool _isScheduleSyncThreadRunning = false;
        private Thread _releaseThread = null;
        private bool _isReleaseThreadRunning = false;
        public ApplicationController()
        {
            SharedClass.HasStopSignal = false;
            SharedClass.IsServiceCleaned = false;
            this.LoadConfig();
            this.UpdateServiceStatus(false);
        }
        public void Start()
        {
            this._seatSyncThread = new Thread(new ThreadStart(this.SyncSeatingChart));
            this._seatSyncThread.Name = "SeatingSync";
            this._seatSyncThread.Start();

            this._scheduleSyncThread = new Thread(new ThreadStart(this.SyncSchedules));
            this._scheduleSyncThread.Name = "ScheduleSync";
            this._scheduleSyncThread.Start();

            this._releaseThread = new Thread(new ThreadStart(this.ReleaseExpiredLockedSeats));
            this._releaseThread.Name = "Release";
            this._releaseThread.Start();
        }
        public void Stop()
        {
            SharedClass.HasStopSignal = true;
            SharedClass.Logger.Info("Processing Stop Signal");            
            while (this._isScheduleSyncThreadRunning)
            {
                SharedClass.Logger.Info(this._scheduleSyncThread.Name + " thread is still running. ThreadState " + this._scheduleSyncThread.ThreadState);                
                if (this._scheduleSyncThread.ThreadState == ThreadState.WaitSleepJoin)
                    this._scheduleSyncThread.Interrupt();
                Thread.Sleep(1000);
            }
            while (this._isSeatSyncThreadRunning)
            {
                SharedClass.Logger.Info(this._seatSyncThread.Name + " thread is still running. ThreadState " + this._seatSyncThread.ThreadState);                
                if (this._seatSyncThread.ThreadState == ThreadState.WaitSleepJoin)
                    this._seatSyncThread.Interrupt();
                Thread.Sleep(1000);
            }
            while (this._isReleaseThreadRunning)
            {
                SharedClass.Logger.Info(this._releaseThread.Name + " thread is still running. ThreadState : " + this._releaseThread.ThreadState);
                if (this._releaseThread.ThreadState == ThreadState.WaitSleepJoin)
                    this._releaseThread.Interrupt();
                Thread.Sleep(1000);
            }
            this.UpdateServiceStatus(true);
            SharedClass.IsServiceCleaned = true;
            SharedClass.Logger.Info("Stop Signal Processed");
        }
        private void SyncSeatingChart()
        {
            SharedClass.Logger.Info("Initializing Objects");
            int threadSleepTime = SharedClass.SeatSyncIntervalInSeconds;
            SqlConnection sqlCon = new SqlConnection(SharedClass.ConnectionString);
            SqlCommand sqlCmd = new SqlCommand(StoredProcedures.GET_UNSYNCED_SEATING_CHART, sqlCon);
            sqlCmd.CommandType = CommandType.StoredProcedure;
            SqlDataAdapter da = null;
            DataSet ds = null;
            XmlDocument xmlDocument = new XmlDocument();
            XmlElement rootElement = xmlDocument.CreateElement("Seats");
            xmlDocument.AppendChild(rootElement);
            this._isSeatSyncThreadRunning = true;
            SharedClass.Logger.Info("Started");
            while (!SharedClass.HasStopSignal)
            {
                try
                {
                    sqlCmd.Parameters.Clear();
                    sqlCmd.Parameters.Add(DataBaseParameters.SUCCESS, SqlDbType.Bit).Direction = ParameterDirection.Output;
                    sqlCmd.Parameters.Add(DataBaseParameters.MESSAGE, SqlDbType.VarChar, 1000).Direction = ParameterDirection.Output;
                    sqlCmd.Parameters.Add(DataBaseParameters.CINEMA_ID, SqlDbType.Int).Direction = ParameterDirection.Output;
                    sqlCmd.Parameters.Add(DataBaseParameters.CINEMA_NAME, SqlDbType.VarChar, 50).Direction = ParameterDirection.Output;
                    sqlCmd.Parameters.Add(DataBaseParameters.NOTIFY_URL, SqlDbType.VarChar, 200).Direction = ParameterDirection.Output;
                    da = new SqlDataAdapter();
                    da.SelectCommand = sqlCmd;
                    ds = new DataSet();
                    da.Fill(ds);
                    if (Convert.ToBoolean(sqlCmd.Parameters[DataBaseParameters.SUCCESS].Value))
                    {
                        if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
                        {
                            SharedClass.Logger.Info("Seats to Sync : " + ds.Tables[0].Rows.Count.ToString());
                            this._lastSeatSyncCount = ds.Tables[0].Rows.Count;
                            rootElement.RemoveAll();
                            rootElement.RemoveAllAttributes();                            
                            rootElement.SetAttribute("CinemaId", sqlCmd.Parameters[DataBaseParameters.CINEMA_ID].Value.ToString());
                            rootElement.SetAttribute("CinemaName", sqlCmd.Parameters[DataBaseParameters.CINEMA_NAME].Value.ToString());
                            foreach (DataRow seatRow in ds.Tables[0].Rows)
                            {
                                XmlElement seatElement = xmlDocument.CreateElement("Seat");
                                foreach (DataColumn seatAttribute in seatRow.Table.Columns)
                                {
                                    seatElement.SetAttribute(seatAttribute.ColumnName, seatRow[seatAttribute.ColumnName].ToString());
                                }
                                rootElement.AppendChild(seatElement);
                            }
                            this.Notify(sqlCmd.Parameters[DataBaseParameters.NOTIFY_URL].Value.ToString(), xmlDocument.OuterXml);
                        }
                    }
                    else
                    {
                        SharedClass.Logger.Error("SeatSync ProcedureCall Unsuccessful. Reason : " + sqlCmd.Parameters[DataBaseParameters.MESSAGE].Value);
                    }
                }
                catch (Exception e)
                {
                    SharedClass.Logger.Error("Exception in SeatSync, Reason : " + e.ToString());
                }
                if (DateTime.Now.Hour < 9)
                    threadSleepTime = 600;
                else
                    if (this._lastSeatSyncCount == 0 && threadSleepTime < 300)
                        threadSleepTime = threadSleepTime + 60;
                    else
                        threadSleepTime = SharedClass.SeatSyncIntervalInSeconds;
                try
                {
                    Thread.Sleep(threadSleepTime * 1000);
                }
                catch (ThreadInterruptedException e)
                { }
                catch (ThreadAbortException e)
                { }
            }
            this._isSeatSyncThreadRunning = false;
            SharedClass.Logger.Info("Exit");            
        }
        private void SyncSchedule_Debug()
        {
            SharedClass.Logger.Info("Initializing Objects");
        }
        private void SyncSchedules()
        {
            try
            {
                SharedClass.Logger.Info("Initializing Objects");
                this._isScheduleSyncThreadRunning = true;
                SqlConnection sqlCon = new SqlConnection(SharedClass.ConnectionString);
                SqlCommand sqlCmd = new SqlCommand(StoredProcedures.GET_UNSYNCED_SCHEDULES, sqlCon);
                sqlCmd.CommandType = CommandType.StoredProcedure;
                SqlDataAdapter da = null;
                DataSet ds = null;
                JArray showsArray = null;
                JObject showObject = null;
                SharedClass.Logger.Info("Started");
                this._isScheduleSyncThreadRunning = true;
                while (!SharedClass.HasStopSignal)
                {
                    try
                    {
                        sqlCmd.Parameters.Clear();
                        sqlCmd.Parameters.Add(DataBaseParameters.SUCCESS, SqlDbType.Bit).Direction = ParameterDirection.Output;
                        sqlCmd.Parameters.Add(DataBaseParameters.MESSAGE, SqlDbType.VarChar, 1000).Direction = ParameterDirection.Output;
                        sqlCmd.Parameters.Add(DataBaseParameters.CINEMA_ID, SqlDbType.Int).Direction = ParameterDirection.Output;
                        sqlCmd.Parameters.Add(DataBaseParameters.CINEMA_NAME, SqlDbType.VarChar, 50).Direction = ParameterDirection.Output;
                        sqlCmd.Parameters.Add(DataBaseParameters.NOTIFY_URL, SqlDbType.VarChar, 200).Direction = ParameterDirection.Output;
                        da = new SqlDataAdapter();
                        da.SelectCommand = sqlCmd;
                        ds = new DataSet();
                        da.Fill(ds);
                        if (Convert.ToBoolean(sqlCmd.Parameters[DataBaseParameters.SUCCESS].Value))
                        {
                            if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
                            {
                                SharedClass.Logger.Info("Schedules To Sync : " + ds.Tables[0].Rows.Count.ToString());
                                this.SyncMovies();
                                showsArray = new JArray();
                                foreach (DataRow showRow in ds.Tables[0].Rows)
                                {
                                    showObject = new JObject();
                                    foreach (DataColumn showProperty in showRow.Table.Columns)
                                    {
                                        showObject.Add(new JProperty(showProperty.ColumnName, showRow[showProperty]));
                                    }
                                    showsArray.Add(showObject);
                                }
                                this.Notify(sqlCmd.Parameters[DataBaseParameters.NOTIFY_URL].Value.ToString(), (new JObject(new JProperty("CinemaId", Convert.ToInt16(sqlCmd.Parameters[DataBaseParameters.CINEMA_ID].Value.ToString())), new JProperty("CinemaName", sqlCmd.Parameters[DataBaseParameters.CINEMA_NAME].Value.ToString()), new JProperty("Shows", showsArray))).ToString());
                            }
                        }
                        else
                        {
                            SharedClass.Logger.Error("ScheduleSync ProcedureCall Unsuccessful. Reason : " + sqlCmd.Parameters[DataBaseParameters.MESSAGE].Value);
                        }
                    }
                    catch (Exception e)
                    {
                        SharedClass.Logger.Error("Exception in ScheduleSync, Reason : " + e.ToString());
                    }
                    try
                    {
                        Thread.Sleep(SharedClass.ScheduleSyncIntervalInSeconds * 1000);
                    }
                    catch (ThreadInterruptedException e)
                    { }
                    catch (ThreadAbortException e)
                    { }
                }
                this._isScheduleSyncThreadRunning = false;
                SharedClass.Logger.Info("Exit");
            }
            catch(Exception e)
            {
                SharedClass.Logger.Error(e.ToString());
            }
            
        }
        private void SyncMovies()
        {
            SqlConnection sqlCon = new SqlConnection(SharedClass.ConnectionString);
            SqlCommand sqlCmd = new SqlCommand(StoredProcedures.GET_MOVIES_TO_SYNC, sqlCon);
            SqlDataAdapter da = null;
            DataSet ds = null;
            XmlDocument xmlDocument = new XmlDocument();
            XmlElement rootElement = xmlDocument.CreateElement("Movies");
            try
            {
                sqlCmd.CommandType = CommandType.StoredProcedure;
                sqlCmd.Parameters.Add(DataBaseParameters.SUCCESS, SqlDbType.Bit).Direction = ParameterDirection.Output;
                sqlCmd.Parameters.Add(DataBaseParameters.MESSAGE, SqlDbType.VarChar, 1000).Direction = ParameterDirection.Output;
                sqlCmd.Parameters.Add(DataBaseParameters.CINEMA_ID, SqlDbType.Int).Direction = ParameterDirection.Output;
                sqlCmd.Parameters.Add(DataBaseParameters.CINEMA_NAME, SqlDbType.VarChar, 50).Direction = ParameterDirection.Output;
                sqlCmd.Parameters.Add(DataBaseParameters.NOTIFY_URL, SqlDbType.VarChar, 200).Direction = ParameterDirection.Output;
                da = new SqlDataAdapter();
                da.SelectCommand = sqlCmd;
                ds = new DataSet();
                da.Fill(ds);
                if (Convert.ToBoolean(sqlCmd.Parameters[DataBaseParameters.SUCCESS].Value))
                {
                    if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
                    {
                        SharedClass.Logger.Info("Movies To Sync : " + ds.Tables[0].Rows.Count.ToString());
                        rootElement.SetAttribute("CinemaId", sqlCmd.Parameters[DataBaseParameters.CINEMA_ID].Value.ToString());
                        rootElement.SetAttribute("CinemaName", sqlCmd.Parameters[DataBaseParameters.CINEMA_NAME].Value.ToString());
                        xmlDocument.AppendChild(rootElement);
                        foreach (DataRow movieRow in ds.Tables[0].Rows)
                        {
                            XmlElement movieElement = xmlDocument.CreateElement("Movie");
                            foreach (DataColumn movieProperty in movieRow.Table.Columns)
                            {
                                movieElement.SetAttribute(movieProperty.ColumnName, movieRow[movieProperty.ColumnName].ToString());
                            }
                            rootElement.AppendChild(movieElement);
                        }
                        this.Notify(sqlCmd.Parameters[DataBaseParameters.NOTIFY_URL].Value.ToString(), xmlDocument.OuterXml);
                    }
                    else
                    {
                        SharedClass.Logger.Info("No Movies found to Sync");
                    }
                }
                else
                {
                    SharedClass.Logger.Error("MoviesSync ProcedureCall unsuccessful. " + sqlCmd.Parameters[DataBaseParameters.MESSAGE].Value.ToString());
                }
            }
            catch (Exception e)
            {
                SharedClass.Logger.Error("Exception while syncing movies. Reason : " + e.ToString());
            }
        }
        private void ReleaseExpiredLockedSeats()
        {
            SharedClass.Logger.Info("Initializing Objects");
            SqlConnection sqlCon = new SqlConnection(SharedClass.ConnectionString);
            SqlCommand sqlCmd = new SqlCommand(StoredProcedures.GET_EXPIRED_LOCKED_SEATS, sqlCon);
            sqlCmd.CommandType = CommandType.StoredProcedure;
            SqlDataAdapter da = null;
            DataSet ds = null;
            XmlDocument xmlDoc = new XmlDocument();
            XmlElement rootElement = xmlDoc.CreateElement("Seats");
            xmlDoc.AppendChild(rootElement);
            SharedClass.Logger.Info("Started");
            this._isReleaseThreadRunning = true;
            while (!SharedClass.HasStopSignal)
            {
                try
                {
                    sqlCmd.Parameters.Clear();
                    sqlCmd.Parameters.Add(DataBaseParameters.SUCCESS, SqlDbType.Bit).Direction = ParameterDirection.Output;
                    sqlCmd.Parameters.Add(DataBaseParameters.MESSAGE, SqlDbType.VarChar, 1000).Direction = ParameterDirection.Output;
                    sqlCmd.Parameters.Add(DataBaseParameters.CINEMA_ID, SqlDbType.Int).Direction = ParameterDirection.Output;
                    sqlCmd.Parameters.Add(DataBaseParameters.CINEMA_NAME, SqlDbType.VarChar, 50).Direction = ParameterDirection.Output;
                    sqlCmd.Parameters.Add(DataBaseParameters.NOTIFY_URL, SqlDbType.VarChar, 200).Direction = ParameterDirection.Output;
                    da = new SqlDataAdapter();
                    da.SelectCommand = sqlCmd;
                    ds = new DataSet();
                    da.Fill(ds);
                    if (Convert.ToBoolean(sqlCmd.Parameters[DataBaseParameters.SUCCESS].Value.ToString()))
                    {
                        if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
                        {
                            rootElement.RemoveAll();
                            rootElement.RemoveAllAttributes();
                            rootElement.SetAttribute("CinemaId", sqlCmd.Parameters[DataBaseParameters.CINEMA_ID].Value.ToString());
                            rootElement.SetAttribute("CinemaName", sqlCmd.Parameters[DataBaseParameters.CINEMA_NAME].Value.ToString());
                            foreach (DataRow seatRow in ds.Tables[0].Rows)
                            {
                                XmlElement seatElement = xmlDoc.CreateElement("Seat");
                                foreach (DataColumn seatProperty in seatRow.Table.Columns)
                                {
                                    seatElement.SetAttribute(seatProperty.ColumnName, seatRow[seatProperty.ColumnName].ToString());
                                }
                                rootElement.AppendChild(seatElement);
                            }
                            this.Notify(sqlCmd.Parameters[DataBaseParameters.NOTIFY_URL].Value.ToString(), xmlDoc.OuterXml);
                        }
                    }
                    else
                    {
                        SharedClass.Logger.Error("Error while getting expired locked seats. " + sqlCmd.Parameters[DataBaseParameters.MESSAGE].Value.ToString());
                    }
                }
                catch (Exception e)
                {
                    SharedClass.Logger.Error("Exception : " + e.ToString());
                }
                try
                {
                    Thread.Sleep(SharedClass.ReleaseCheckIntervalInSeconds * 1000);
                }
                catch (Exception e)
                { }
            }
            this._isReleaseThreadRunning = false;
            SharedClass.Logger.Info("Exit");
        }
        private void Notify(string notifyUrl, string data)
        {
            string notifyId = System.Guid.NewGuid().ToString();
            SharedClass.Logger.Info("Notifying (" + notifyId + ") To " + notifyUrl + " With Payload : " + data);
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            StreamReader streamReader = null;
            StreamWriter streamWriter = null;
            CredentialCache credentialCache = null;
            byte attempt = 1;
            retry:
            try
            {
                if (SharedClass.HasStopSignal)
                    attempt = SharedClass.NotifyMaxFailedAttempts;
                
                request = WebRequest.Create(notifyUrl) as HttpWebRequest;
                request.Method = HttpMethod.POST;
                request.Timeout = 120 * 1000;//2 Minutes
                if (SharedClass.NotifyAuthUserName.Length > 0 && SharedClass.NotifyAuthPassword.Length > 0)
                {
                    credentialCache = new CredentialCache();
                    credentialCache.Add(new Uri(notifyUrl), "Basic", new NetworkCredential(SharedClass.NotifyAuthUserName, SharedClass.NotifyAuthPassword));
                    request.Credentials = credentialCache;
                }
                request.ContentType = "application/x-www-form-urlencoded";
                streamWriter = new StreamWriter(request.GetRequestStream());
                streamWriter.Write(data);
                streamWriter.Flush();
                streamWriter.Close();
                response = request.GetResponse() as HttpWebResponse;
                streamReader = new StreamReader(response.GetResponseStream());
                SharedClass.Logger.Info("Response : " + streamReader.ReadToEnd());
                streamReader.Close();
            }
            catch (Exception e)
            {
                SharedClass.Logger.Error("Exception while notifying (" + notifyId + ") : " + e.ToString());
                if (attempt <= SharedClass.NotifyMaxFailedAttempts)
                {
                    ++attempt;
                    Thread.Sleep(10000);
                    SharedClass.Logger.Info("Retry Attempt : " + attempt.ToString());
                    goto retry;
                }
            }
        }
        private void LoadConfig()
        {
            SharedClass.InitializeLogger();
            SharedClass.ConnectionString = System.Configuration.ConfigurationManager.ConnectionStrings["ConnectionString"].ConnectionString;
            SharedClass.Logger.Info("ConnectionString : " + SharedClass.ConnectionString);
            if (System.Configuration.ConfigurationManager.AppSettings["NotifyMaxFailedAttempts"] != null)
            {
                byte tempValue = SharedClass.NotifyMaxFailedAttempts;
                if (byte.TryParse(System.Configuration.ConfigurationManager.AppSettings["NotifyMaxFailedAttempts"].ToString(), out tempValue))
                    SharedClass.NotifyMaxFailedAttempts = tempValue;
            }
            SharedClass.Logger.Info("NotifyMaxFailedAttempts : " + SharedClass.NotifyMaxFailedAttempts);
            if (System.Configuration.ConfigurationManager.AppSettings["ScheduleSyncIntervalInSeconds"] != null)
            {
                int tempValue = SharedClass.ScheduleSyncIntervalInSeconds;
                if (int.TryParse(System.Configuration.ConfigurationManager.AppSettings["ScheduleSyncIntervalInSeconds"].ToString(), out tempValue))
                    SharedClass.ScheduleSyncIntervalInSeconds = tempValue;
            }
            SharedClass.Logger.Info("ScheduleSyncIntervalInSeconds : " + SharedClass.ScheduleSyncIntervalInSeconds.ToString());
            if (System.Configuration.ConfigurationManager.AppSettings["SeatSyncIntervalInSeconds"] != null)
            {
                int tempValue = SharedClass.SeatSyncIntervalInSeconds;
                if (int.TryParse(System.Configuration.ConfigurationManager.AppSettings["SeatSyncIntervalInSeconds"].ToString(), out tempValue))
                    SharedClass.SeatSyncIntervalInSeconds = tempValue;
            }
            SharedClass.Logger.Info("SeatSyncIntervalInSeconds : " + SharedClass.SeatSyncIntervalInSeconds.ToString());
            if (System.Configuration.ConfigurationManager.AppSettings["ReleaseCheckIntervalInSeconds"] != null)
            {
                int tempValue = SharedClass.ReleaseCheckIntervalInSeconds;
                if (int.TryParse(System.Configuration.ConfigurationManager.AppSettings["ReleaseCheckIntervalInSeconds"].ToString(), out tempValue))
                    SharedClass.ReleaseCheckIntervalInSeconds = tempValue;
            }
            SharedClass.Logger.Info("ReleaseCheckIntervalInSeconds : " + SharedClass.ReleaseCheckIntervalInSeconds.ToString());
        }
        private void UpdateServiceStatus(bool isStopped)
        {
            SharedClass.Logger.Info("Updating ServiceStatus In Database. IsStopped : " + isStopped.ToString());
            SqlConnection sqlCon = new SqlConnection(SharedClass.ConnectionString);
            SqlCommand sqlCmd = new SqlCommand(StoredProcedures.UPDATE_SERVICE_STATUS, sqlCon);
            try
            {
                string serviceName = this.GetServiceName();
                serviceName = serviceName.Length > 0 ? serviceName : "BookingSync";
                SharedClass.Logger.Info("Service Name : " + serviceName);

                sqlCmd.CommandType = CommandType.StoredProcedure;
                sqlCmd.Parameters.Add(DataBaseParameters.SERVICE_NAME, SqlDbType.VarChar, 32).Value = serviceName;
                sqlCmd.Parameters.Add(DataBaseParameters.IS_STOPPED, SqlDbType.Bit).Value = isStopped;
                sqlCon.Open();
                sqlCmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                SharedClass.Logger.Error("Exception while updating service status. " + e.ToString());
            }
            finally
            {
                if (sqlCon.State == ConnectionState.Open)
                    sqlCon.Close();
                try
                {
                    sqlCmd.Dispose();
                    sqlCon.Dispose();
                }
                catch (Exception e)
                {
                }
            }
        }

        private string GetServiceName()
        {
            string serviceName = string.Empty;
            try
            {
                int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Service where ProcessId = " + processId);
                System.Management.ManagementObjectCollection collection = searcher.Get();
                serviceName = (string)collection.Cast<System.Management.ManagementBaseObject>().First()["Name"];
            }
            catch (Exception e)
            {
                serviceName = string.Empty;
                SharedClass.Logger.Error(string.Format("Exception while fetching the service name from OS. Reason : {0}", e.ToString()));
            }
            return serviceName;
        }
    }
}
