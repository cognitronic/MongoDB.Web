using System;
using System.Collections.Specialized;
using System.IO;
using System.Web;
using System.Web.Configuration;
using System.Web.Hosting;
using System.Web.SessionState;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;

namespace MongoDB.Web.Providers
{
    public class MongoDBSessionStateProvider : SessionStateStoreProviderBase
    {
        private MongoCollection mongoCollection;
        private SessionStateSection sessionStateSection;

        public override SessionStateStoreData CreateNewStoreData(HttpContext context, int timeout)
        {
            return new SessionStateStoreData(new SessionStateItemCollection(), SessionStateUtility.GetSessionStaticObjects(context), timeout);
        }

        public override void CreateUninitializedItem(HttpContext context, string id, int timeout)
        {
            var memoryStream = new MemoryStream();

            using (var binaryWriter = new BinaryWriter(memoryStream))
            {
                var query = Query.And(Query.EQ("applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath), Query.EQ("id", id));
                this.mongoCollection.Remove(query);

                var bsonDocument = new BsonDocument
                    {
                        { "applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath },
                        { "created", DateTime.Now },
                        { "expires", DateTime.Now.AddMinutes(1400) },
                        { "id", id },
                        { "lockDate", DateTime.Now },
                        { "locked", false },
                        { "lockId", 0 },
                        { "sessionStateActions", SessionStateActions.None },
                        { "sessionStateItems", memoryStream.ToArray() },
                        { "sessionStateItemsCount", 0 },
                        { "timeout", 20 }
                    };

                this.mongoCollection.Insert(bsonDocument);
            }
        }

        public override void Dispose()
        {
        }

        public override void EndRequest(HttpContext context)
        {
        }

        public override SessionStateStoreData GetItem(HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {
            return this.GetSessionStateStoreData(false, context, id, out locked, out lockAge, out lockId, out actions);
        }

        public override SessionStateStoreData GetItemExclusive(HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {
            return this.GetSessionStateStoreData(true, context, id, out locked, out lockAge, out lockId, out actions);
        }

        public override void Initialize(string name, NameValueCollection config)
        {
            var configuration = WebConfigurationManager.OpenWebConfiguration(HostingEnvironment.ApplicationVirtualPath);
            this.sessionStateSection = configuration.GetSection("system.web/sessionState") as SessionStateSection;

            this.mongoCollection = new MongoClient(config["connectionString"] ?? "mongodb://localhost").GetServer().GetDatabase(config["database"] ?? "ASPNETDB").GetCollection(config["collection"] ?? "SessionState");
            this.mongoCollection.EnsureIndex("applicationVirtualPath", "id");
            this.mongoCollection.EnsureIndex("applicationVirtualPath", "id", "lockId");

            base.Initialize(name, config);
        }

        public override void InitializeRequest(HttpContext context)
        {
        }

        public override void ReleaseItemExclusive(HttpContext context, string id, object lockId)
        {
            var query = Query.And(Query.EQ("applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath), Query.EQ("id", id), Query.EQ("lockId", lockId.ToString()));
            var update = Update.Set("expires", DateTime.Now.Add(this.sessionStateSection.Timeout)).Set("locked", false);
            this.mongoCollection.Update(query, update);
        }

        public override void RemoveItem(HttpContext context, string id, object lockId, SessionStateStoreData item)
        {
            var query = Query.And(Query.EQ("applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath), Query.EQ("id", id), Query.EQ("lockId", lockId.ToString()));
            this.mongoCollection.Remove(query);
        }

        public override void ResetItemTimeout(HttpContext context, string id)
        {
            var query = Query.And(Query.EQ("applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath), Query.EQ("id", id));
            var update = Update.Set("expires", DateTime.Now.Add(this.sessionStateSection.Timeout));
            this.mongoCollection.Update(query, update);
        }

        public override void SetAndReleaseItemExclusive(HttpContext context, string id, SessionStateStoreData item, object lockId, bool newItem)
        {
            var memoryStream = new MemoryStream();

            using (var binaryWriter = new BinaryWriter(memoryStream))
            {
                ((SessionStateItemCollection)item.Items).Serialize(binaryWriter);

                if (newItem)
                {
                    var query = Query.And(Query.EQ("applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath), Query.EQ("id", id));
                    this.mongoCollection.Remove(query);

                    var bsonDocument = new BsonDocument
                    {
                        { "applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath },
                        { "created", DateTime.Now },
                        { "expires", DateTime.Now.AddMinutes(item.Timeout) },
                        { "id", id },
                        { "lockDate", DateTime.Now },
                        { "locked", false },
                        { "lockId", 0 },
                        { "sessionStateActions", SessionStateActions.None },
                        { "sessionStateItems", memoryStream.ToArray() },
                        { "sessionStateItemsCount", item.Items.Count },
                        { "timeout", item.Timeout }
                    };

                    this.mongoCollection.Insert(bsonDocument);
                }
                else
                {
                    var query = Query.And(Query.EQ("applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath), Query.EQ("id", id), Query.EQ("lockId", lockId.ToString()));
                    var upate = Update.Set("expires", DateTime.Now.Add(this.sessionStateSection.Timeout)).Set("items", memoryStream.ToArray()).Set("locked", false).Set("sessionStateItemsCount", item.Items.Count);
                    this.mongoCollection.Update(query, upate);
                }
            }
        }

        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            return false;
        }

        #region Private Methods

        private SessionStateStoreData GetSessionStateStoreData(bool exclusive, HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {
            SessionStateStoreData item = null;
            actions = SessionStateActions.None;
            lockAge = TimeSpan.Zero;
            locked = false;
            lockId = null;
            
            var query = Query.And(Query.EQ("applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath), Query.EQ("id", id));
            var bsonDocument = this.mongoCollection.FindOneAs<BsonDocument>(query);

            if (bsonDocument == null)
            {
                locked = false;
            }
            else if (bsonDocument["expires"].ToUniversalTime() <= DateTime.Now)
            {
                locked = false;
                this.mongoCollection.Remove(Query.And(Query.EQ("applicationVirtualPath", HostingEnvironment.ApplicationVirtualPath), Query.EQ("id", id)));
            }
            else if (bsonDocument["locked"].AsBoolean == true)
            {
                lockAge = DateTime.Now.Subtract(bsonDocument["lockDate"].ToUniversalTime());
                locked = true;
                lockId = bsonDocument["lockId"].AsInt32;
            }
            else
            {
                locked = false;
                lockId = bsonDocument["lockId"].AsInt32;
                actions = (SessionStateActions)bsonDocument["sessionStateActions"].AsInt32;
            }

            if (exclusive && bsonDocument != null)
            {
                lockId = (int)lockId + 1;
                actions = SessionStateActions.None;

                var update = Update.Set("lockDate", DateTime.Now).Set("lockId", (int)lockId).Set("locked", true).Set("sessionStateActions", SessionStateActions.None);
                this.mongoCollection.Update(query, update);
            }

            if (actions == SessionStateActions.InitializeItem)
            {
                return this.CreateNewStoreData(context, this.sessionStateSection.Timeout.Minutes);
            }
            if (bsonDocument != null)
            {
                using (var memoryStream = new MemoryStream(bsonDocument["sessionStateItems"].AsByteArray))
                {
                    var sessionStateItems = new SessionStateItemCollection();

                    if (memoryStream.Length > 0)
                    {
                        var binaryReader = new BinaryReader(memoryStream);
                        sessionStateItems = SessionStateItemCollection.Deserialize(binaryReader);
                    }

                    return new SessionStateStoreData(sessionStateItems, SessionStateUtility.GetSessionStaticObjects(context), bsonDocument["timeout"].AsInt32);
                }
            }
            return null;
        }

        #endregion
    }
}
