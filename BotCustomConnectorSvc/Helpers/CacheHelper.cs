using BotCustomConnectorSvc.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Table.Queryable;

namespace BotCustomConnectorSvc
{
    public static class Helpers
    {
        static bool isMixed = bool.Parse(ConfigurationManager.AppSettings["ChannelModeMixed"]);
        static int counter = 0;
        static Token token;
        public static string GetJwtToken(App app)
        {
            if (string.IsNullOrEmpty(app.AppId) || string.IsNullOrEmpty(app.AppKey))
            {
                return string.Empty;
            }

            if (token == null || token.ExpiryUtc < DateTime.UtcNow)
            {
                HttpClient _client = new HttpClient();
                _client.DefaultRequestHeaders.Accept.Clear();
                _client.BaseAddress = new Uri("https://login.microsoftonline.com/");
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", app.AppId),
                    new KeyValuePair<string, string>("client_secret", app.AppKey),
                    new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/.default")
                });
                var response = _client.PostAsync("/botframework.com/oauth2/v2.0/token", content).Result;
                token = JsonConvert.DeserializeObject<Token>(response.Content.ReadAsStringAsync().Result);
                token.ExpiryUtc = DateTime.UtcNow.AddSeconds(3599);
            }

            return token.Access_token;
        }

        public static string ChannelId
        {
            get
            {
                if (!isMixed)
                {
                    return "emulator";
                }

                counter++;

                switch (counter % 4)
                {
                    case 0:
                        counter = 0;
                        return "emulator";
                    case 1:
                        return "webchat";                        
                    case 2:
                        return "directline";
                    case 3:
                        return "skype";
                }

                return "emulator";
            }
        }
    }

    public static class CacheHelper
    {
        static CloudStorageAccount account = default(CloudStorageAccount);
        private static string partitionKey = default(string);
        static CloudTableClient tableClient;
        static CloudTable botStateTable, botConvTable;
        static bool storageExists = false;

        static bool dataCleanupEnabled = bool.Parse(ConfigurationManager.AppSettings["DataCleanupEnabled"]);


        static CacheHelper()
        {
            string azureStoragAccount = ConfigurationManager.AppSettings["AzureStorageAccount"];
            string azureStorageSecret = ConfigurationManager.AppSettings["AzureStorageSecret"];

            if (!string.IsNullOrEmpty(azureStoragAccount) && !string.IsNullOrEmpty(azureStorageSecret))
            {
                account = new CloudStorageAccount(new StorageCredentials(azureStoragAccount, azureStorageSecret), true);

                partitionKey = DateTime.Now.ToString("dd_MM_yyyy_hh_mm_ss");
                tableClient = account.CreateCloudTableClient();

                // Create the botStateTable if it doesn’t exist. 
                botStateTable = tableClient.GetTableReference("BotState");
                botStateTable.CreateIfNotExistsAsync();

                // Create the botStateTable if it doesn’t exist. 
                botConvTable = tableClient.GetTableReference("BotConversationData");
                botConvTable.CreateIfNotExistsAsync();
                storageExists = true;
            }
        }

        public static int UpdateConversation(string conversationId, Activity activity)
        {
            bool update = true;
            Conversation conv = ReadConvFromStorage(conversationId);

            if (conv == null)
            {
                conv = new Conversation() { Id = conversationId };
                update = false;
            }

            int sequence = conv.Activities.Count > 0 ? conv.Activities.Keys.Max(): 0;
            activity.Id = $"{conversationId}|{++sequence}";
            conv.Activities.Add(sequence, activity);

            WriteConversationToStorage(conv, update);

            return sequence;
        }

        public static Conversation GetConversation(string conversationId, int watermark)
        {
            Conversation conv = ReadConvFromStorage(conversationId);

            if (conv == null)
            {
                conv = new Conversation();
            }

            if (watermark > 0)
            {
                Conversation outPut = new Conversation() { Id = conversationId };
                foreach (KeyValuePair<int, Activity> act in conv.Activities.Where(a => a.Key > watermark))
                {
                    outPut.Activities.Add(act.Key, act.Value);
                }

                return outPut;
            }
            else
            {
                return conv;
            }
        }

        private static void UpdateState(string key, StateData data)
        {           
            WriteStateToStorage(key, data);
        }

        public static void UpdateConversationState(string conversationId, StateData data)
        {
            UpdateState(conversationId, data);
        }

        public static void UpdateConversationUserState(string conversationId, string userId, StateData data)
        {
            string key = $"{conversationId}_{userId}";
            UpdateState(key, data);
        }

        public static void UpdateUserState(string userId, StateData data)
        {
            UpdateState(userId, data);
        }

        private static StateData GetStateData(string key)
        {
            StateData stateData = ReadStateFromStorage(key);

            return stateData;
        }

        public static StateData GetConversationUserState(string conversationId, string userId)
        {
            string key = $"{conversationId}_{userId}";
            return GetStateData(key);
        }

        public static StateData GetConversationState(string conversationId)
        {
            return GetStateData(conversationId);
        }

        public static StateData GetUserState(string userId)
        {
            return GetStateData(userId);
        }

        public static string ClearAllConvData()
        {
            if (dataCleanupEnabled)
            {
                DeleteAllConvFromStorage();
                return $"conversation data deleted";
            }
            else
            {
                return "Data cleanup disabled";
            }
        }

        public static string ClearConvData(string convId)
        {
            if (dataCleanupEnabled)
            {
                DeleteConversationFromStorage(convId);
                return $"conversation data deleted";
            }
            else
            {
                return "Data cleanup disabled";
            }
        }

        public static string ClearAllConvStateData()
        {
            if (dataCleanupEnabled)
            {
                ClearAllConvStateData();
                return $"State Data deleted";
            }
            else
            {
                return "Data cleanup disabled";
            }
        }

        public static string ClearConvStateData(string convId)
        {
            if (dataCleanupEnabled)
            {
                DeleteStateDataFromStorage(convId);
                return "State data deleted";
            }
            else
            {
                return "Data cleanup disabled";
            }
        }

        private static void WriteConversationToStorage(Conversation conv, bool update)
        {
            Entity entity = new Entity(partitionKey, conv.Id)
            {
                ETag = "*" ,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(conv)
            };

            TableOperation operation = update ? TableOperation.Replace(entity) : TableOperation.InsertOrMerge(entity);

            // Execute the insert operation. 
            botConvTable.ExecuteAsync(operation);
        }

        private static Conversation ReadConvFromStorage(string convId)
        {
            // Create the TableOperation that inserts the customer entity.
            TableOperation retrieve = TableOperation.Retrieve(partitionKey, convId);
            TableResult result = botConvTable.Execute(retrieve);

            Conversation conversation = null;
            if (result != null && result.Result != null)
            {
                DynamicTableEntity entity = result.Result as DynamicTableEntity;
                conversation = Newtonsoft.Json.JsonConvert.DeserializeObject<Conversation>(entity.Properties["Data"].StringValue);
            }

            return conversation;
        }

        private static void DeleteAllConvFromStorage()
        {
            try
            {
                var entityPattern = new DynamicTableEntity();
                entityPattern.PartitionKey = partitionKey;
                entityPattern.ETag = "*";

                botConvTable.ExecuteAsync(TableOperation.Delete(entityPattern));
            }
            catch
            { }
        }

        private static void DeleteConversationFromStorage(string convId)
        {
            try
            {
                Entity entity = new Entity(partitionKey, convId) { ETag = "*" };
                TableOperation delete = TableOperation.Delete(entity);
                botConvTable.ExecuteAsync(delete);
            }
            catch
            { }
        }

        private static void WriteStateToStorage(string key, StateData data)
        {
            // Create the TableOperation that inserts the customer entity.
            TableOperation retrieve = TableOperation.Retrieve(partitionKey, key);
            TableResult result = botStateTable.Execute(retrieve);

            bool update = result != null && result.Result != null;

            Entity entity = new Entity(partitionKey, key)
            {
                ETag = "*" ,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(data)
            };

            TableOperation operation = update ? TableOperation.Replace(entity) : TableOperation.InsertOrMerge(entity);

            // Execute the insert operation. 
            botStateTable.ExecuteAsync(operation);
        }

        private static StateData ReadStateFromStorage(string key)
        {
            // Create the TableOperation that inserts the customer entity.
            TableOperation retrieve = TableOperation.Retrieve(partitionKey, key);
            TableResult result = botStateTable.Execute(retrieve);

            StateData stateData = null;
            if (result != null && result.Result != null)
            {
                DynamicTableEntity entity = result.Result as DynamicTableEntity;
                stateData = Newtonsoft.Json.JsonConvert.DeserializeObject<StateData>(entity.Properties["Data"].StringValue);
            }

            return stateData;
        }

        private static void DeleteAllStateDataStorage()
        {
            try
            {
                var entityPattern = new DynamicTableEntity();
                entityPattern.PartitionKey = partitionKey;
                entityPattern.ETag = "*";

                botStateTable.ExecuteAsync(TableOperation.Delete(entityPattern));
            }
            catch
            {
            }
        }

        private static void DeleteStateDataFromStorage(string key)
        {
            try
            {
                Entity entity = new Entity(partitionKey, key) { ETag = "*" };
                TableOperation delete = TableOperation.Delete(entity);
                botStateTable.ExecuteAsync(delete);
            }
            catch
            {

            }           
        }
    }

    public class Entity : TableEntity
    {
        public Entity(string partitionKey, string convId)
        {
            this.PartitionKey = partitionKey;
            this.RowKey = convId;
        }

        public string Data { get; set; }
    }
}
