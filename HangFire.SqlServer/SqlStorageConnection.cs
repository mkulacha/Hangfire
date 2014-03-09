using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using HangFire.Common;
using HangFire.Server;
using HangFire.SqlServer.Entities;
using HangFire.Storage;

namespace HangFire.SqlServer
{
    public class SqlStorageConnection : IStorageConnection
    {
        private readonly SqlConnection _connection;

        public SqlStorageConnection(SqlServerStorage storage, SqlConnection connection)
        {
            _connection = connection;
            Jobs = new SqlStoredJobs(_connection);
            Sets = new SqlStoredSets(_connection);
            Storage = storage;
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        public IAtomicWriteTransaction CreateWriteTransaction()
        {
            return new SqlWriteTransaction(_connection);
        }

        public IJobFetcher CreateFetcher(IEnumerable<string> queueNames)
        {
            return new SqlServerFetcher(_connection, queueNames);
        }

        public IDisposable AcquireJobLock(string jobId)
        {
            return new SqlJobLock(jobId, _connection);
        }

        public IStoredJobs Jobs { get; private set; }
        public IStoredSets Sets { get; private set; }
        public JobStorage Storage { get; private set; }

        public string CreateExpiredJob(IDictionary<string, string> parameters, TimeSpan expireIn)
        {
            var data = new InvocationData
            {
                Method = parameters["Method"],
                ParameterTypes = parameters["ParameterTypes"],
                Type = parameters["Type"]
            };

            const string createJobSql = @"
insert into HangFire.Job (State, InvocationData, Arguments, CreatedAt, ExpireAt)
values (@state, @invocationData, @arguments, @createdAt, @expireAt);
SELECT CAST(SCOPE_IDENTITY() as int)";

            return _connection.Query<int>(
                createJobSql,
                new
                {
                    state = "Created",
                    invocationData = JobHelper.ToJson(data),
                    arguments = parameters["Arguments"],
                    createdAt = JobHelper.FromStringTimestamp(parameters["CreatedAt"]),
                    expireAt = DateTime.UtcNow.Add(expireIn)
                }).Single().ToString();
        }

        public void AnnounceServer(string serverId, int workerCount, IEnumerable<string> queues)
        {
            var data = new ServerData
            {
                WorkerCount = workerCount,
                Queues = queues.ToArray()
            };

            _connection.Execute(
                @"merge HangFire.Server as Target "
                + @"using (VALUES (@id, @data)) as Source (Id, Data) "
                + @"on Target.Id = Source.Id "
                + @"when matched then update set Data = Source.Data, LastHeartbeat = null "
                + @"when not matched then insert (Id, Data) values (Source.Id, Source.Data);",
                new { id = serverId, data = JobHelper.ToJson(data) });
        }

        public void RemoveServer(string serverId)
        {
            _connection.Execute(
                @"delete from HangFire.Server where Id = @id",
                new { id = serverId });
        }

        public void Heartbeat(string serverId)
        {
            _connection.Execute(
                @"update HangFire.Server set LastHeartbeat = @now where Id = @id",
                new { now = DateTime.UtcNow, id = serverId });
        }

        public void RemoveTimedOutServers(TimeSpan timeOut)
        {
            _connection.Execute(
                @"delete from HangFire.Server where LastHeartbeat < @timeOutAt",
                new { timeOutAt = DateTime.UtcNow.Add(timeOut.Negate()) });
            // TODO: log it
        }
    }
}