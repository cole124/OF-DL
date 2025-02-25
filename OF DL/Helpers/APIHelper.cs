using Microsoft.CSharp.RuntimeBinder;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OF_DL.Entities;
using OF_DL.Entities.Archived;
using OF_DL.Entities.Highlights;
using OF_DL.Entities.Lists;
using OF_DL.Entities.Messages;
using OF_DL.Entities.Post;
using OF_DL.Entities.Purchased;
using OF_DL.Entities.Stories;
using OF_DL.Entities.Streams;
using OF_DL.Enumurations;
using Serilog;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WidevineClient.Widevine;
using static WidevineClient.HttpUtil;

namespace OF_DL.Helpers;

public class APIHelper : IAPIHelper
{
    private static readonly JsonSerializerSettings m_JsonSerializerSettings;
    private readonly IDBHelper m_DBHelper;
    private readonly Auth auth;

    public List<int> ignoredUsers = new List<int>();
    public string[] ignoredText = new string[] { "#announcement", "#ad", " she " };
    public string[] IgnoredLists = new[] { "Ignored"};
    public List<string> promoLinks = new List<string>();

    static APIHelper()
    {
        m_JsonSerializerSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Ignore
        };
    }

    public APIHelper(Auth auth, IDownloadConfig downloadConfig)
    {
        this.auth = auth;
        m_DBHelper = new DBHelper(downloadConfig, $"{Environment.CurrentDirectory}/{auth.USER_ID}/OnlyFans/");
    }


    public Dictionary<string, string> GetDynamicHeaders(string path, string queryParams)
    {
        Log.Debug("Calling GetDynamicHeaders");
        Log.Debug($"Path: {path}");
        Log.Debug($"Query Params: {queryParams}");

        DynamicRules? root;

        root = JsonConvert.DeserializeObject<DynamicRules>(File.ReadAllText("rules.json"));

        DateTimeOffset dto = (DateTimeOffset)DateTime.UtcNow;
        long timestamp = dto.ToUnixTimeMilliseconds();

        string input = $"{root!.StaticParam}\n{timestamp}\n{path + queryParams}\n{auth.USER_ID}";
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = SHA1.HashData(inputBytes);
        string hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();

        var checksum = root.ChecksumIndexes.Aggregate(0, (current, number) => current + hashString[number]) + root.ChecksumConstant!.Value;
        var sign = $"{root.Prefix}:{hashString}:{checksum.ToString("X").ToLower()}:{root.Suffix}";

        Dictionary<string, string> headers = new()
        {
            { "accept", "application/json, text/plain" },
            { "app-token", root.AppToken! },
            { "cookie", auth!.COOKIE! },
            { "sign", sign },
            { "time", timestamp.ToString() },
            { "user-id", auth!.USER_ID! },
            { "user-agent", auth!.USER_AGENT! },
            { "x-bc", auth!.X_BC! }
        };
        return headers;
    }


    private async Task<string?> BuildHeaderAndExecuteRequests(Dictionary<string, string> getParams, string endpoint, HttpClient client)
    {
        Log.Debug("Calling BuildHeaderAndExecuteRequests");

        
        string body="";
        int cnt = 0;
        while(cnt<10)
        {
            cnt++;
            HttpRequestMessage request = await BuildHttpRequestMessage(getParams, endpoint);
            using var response = await client.SendAsync(request);
            if (response.StatusCode==HttpStatusCode.TooManyRequests)
            {
                Thread.Sleep(cnt * 2000);
            } else
            {
                try
                {
                    response.EnsureSuccessStatusCode();
                    body = await response.Content.ReadAsStringAsync();
                    break;
                }
                catch (Exception ex)
                {
                    break;
                }
            }
                
            
        }
        
        

        Log.Debug(body);

        return body;
    }

    private async Task<string?> BuildHeaderAndExecutePosts(Dictionary<string, string> getParams, string endpoint, HttpClient client)
    {
        Log.Debug("Calling BuildHeaderAndExecuteRequests");

        string jStr = "{\"code\":\"" + getParams["code"] + "\",\"reserve\":false}";

        HttpRequestMessage request = await BuildHttpRequestMessage(new Dictionary<string, string>(), endpoint);
        request.Method = HttpMethod.Post;
        request.Headers.Add("Accept", "application/json");
        
        request.Content= new StringContent(
    jStr,//JsonConvert.SerializeObject(getParams).ToString(),
    Encoding.UTF8,
    "application/json"
);
        using var response = await client.SendAsync(request);
        try
        {
            response.EnsureSuccessStatusCode();
        } catch (Exception ex) { }
        
        string body = await response.Content.ReadAsStringAsync();

        Log.Debug(body);

        return body;
    }


    private async Task<HttpRequestMessage> BuildHttpRequestMessage(Dictionary<string, string> getParams, string endpoint)
    {
        Log.Debug("Calling BuildHttpRequestMessage");

        string queryParams = "?" + string.Join("&", getParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));

        Dictionary<string, string> headers = GetDynamicHeaders($"/api2/v2{endpoint}", queryParams);

        HttpRequestMessage request = new(HttpMethod.Get, $"{Constants.API_URL}{endpoint}{queryParams}");

        Log.Debug($"Full request URL: {Constants.API_URL}{endpoint}{queryParams}");

        foreach (KeyValuePair<string, string> keyValuePair in headers)
        {
            request.Headers.Add(keyValuePair.Key, keyValuePair.Value);
        }

        return request;
    }

    private static double ConvertToUnixTimestampWithMicrosecondPrecision(DateTime date)
    {
        DateTime origin = new(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        TimeSpan diff = date.ToUniversalTime() - origin;

        return diff.TotalSeconds;  // This gives the number of seconds. If you need milliseconds, use diff.TotalMilliseconds
    }

    public static bool IsStringOnlyDigits(string input)
    {
        return input.All(char.IsDigit);
    }


    private static HttpClient GetHttpClient(IDownloadConfig? config = null)
    {
        var client = new HttpClient();
        if (config?.Timeout != null && config.Timeout > 0)
        {
            client.Timeout = TimeSpan.FromSeconds(config.Timeout.Value);
        }
        return client;
    }


    /// <summary>
    /// this one is used during initialization only
    /// if the config option is not available then no modificatiotns will be done on the getParams
    /// </summary>
    /// <param name="config"></param>
    /// <param name="getParams"></param>
    /// <param name="dt"></param>
    private static void UpdateGetParamsForDateSelection(Enumerations.DownloadDateSelection downloadDateSelection, ref Dictionary<string, string> getParams, DateTime? dt)
    {
        //if (config.DownloadOnlySpecificDates && dt.HasValue)
        //{
        if (dt.HasValue)
        {
            UpdateGetParamsForDateSelection(
                downloadDateSelection,
                ref getParams,
                ConvertToUnixTimestampWithMicrosecondPrecision(dt.Value).ToString("0.000000", CultureInfo.InvariantCulture)
            );
        }
        //}
    }

    private static void UpdateGetParamsForDateSelection(Enumerations.DownloadDateSelection downloadDateSelection, ref Dictionary<string, string> getParams, string unixTimeStampInMicrosec)
    {
        switch (downloadDateSelection)
        {
            case Enumerations.DownloadDateSelection.before:
                getParams["beforePublishTime"] = unixTimeStampInMicrosec;
                break;
            case Enumerations.DownloadDateSelection.after:
                getParams["order"] = "publish_date_asc";
                getParams["afterPublishTime"] = unixTimeStampInMicrosec;
                break;
        }
    }


    public async Task<User?> GetUserInfo(string endpoint)
    {
        Log.Debug($"Calling GetUserInfo: {endpoint}");

        try
        {
            Entities.User? user = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "publish_date_asc" }
            };

            HttpClient client = new();
            HttpRequestMessage request = await BuildHttpRequestMessage(getParams, endpoint);

            using var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return user;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            user = JsonConvert.DeserializeObject<Entities.User>(body, m_JsonSerializerSettings);
            return user;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }

    public async Task<JObject> GetUserInfoById(string endpoint)
    {
        try
        {
            HttpClient client = new();
            HttpRequestMessage request = await BuildHttpRequestMessage(new Dictionary<string, string>(), endpoint);

            using var response = await client.SendAsync(request);

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();

            //if the content creator doesnt exist, we get a 200 response, but the content isnt usable
            //so let's not throw an exception, since "content creator no longer exists" is handled elsewhere
            //which means we wont get loads of exceptions
            if (body.Equals("[]"))
                return null;

            JObject jObject = JObject.Parse(body);

            return jObject;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<Dictionary<string, (int id, DateTime expiration)>?> GetAllSubscriptions(Dictionary<string, string> getParams, string endpoint, bool includeRestricted, IDownloadConfig config)
    {
        try
        {
            Dictionary<string, (int id,DateTime expiration,string? price)> users = new();
            Subscriptions subscriptions = new();
            Subscriptions ignoredSubscriptions = new();

            Log.Debug("Calling GetAllSubscrptions");

            string? body = await BuildHeaderAndExecuteRequests(getParams, endpoint, new HttpClient());

            subscriptions = JsonConvert.DeserializeObject<Subscriptions>(body);
            if (subscriptions != null && subscriptions.hasMore)
            {
                getParams["offset"] = subscriptions.list.Count.ToString();

                while (true)
                {
                    Subscriptions newSubscriptions = new();
                    string? loopbody = await BuildHeaderAndExecuteRequests(getParams, endpoint, new HttpClient());

                    if (!string.IsNullOrEmpty(loopbody) && loopbody.Trim() != "[]")
                    {
                        newSubscriptions = JsonConvert.DeserializeObject<Subscriptions>(loopbody, m_JsonSerializerSettings);
                    }
                    else
                    {
                        break;
                    }

                    //subscriptions.list.AddRange(newSubscriptions.list.Where(s => s.collections.Count() == 0 || IgnoredLists.Intersect(s.collections, StringComparer.OrdinalIgnoreCase).Count() == 0).ToList());
                    subscriptions.list.AddRange(newSubscriptions.list);
                    //if(subscriptions.list.Select(su => su.id).Contains(40179626))
                    //{
                    //    var sub= subscriptions.list.FirstOrDefault(su => su.id==40179626);
                    //}
                    ignoredSubscriptions.list.AddRange(newSubscriptions.list.Where(s => s.collections.Count() > 0 && IgnoredLists.Intersect(s.collections, StringComparer.OrdinalIgnoreCase).Count()>0).ToList());
                    if (!newSubscriptions.hasMore)
                    {
                        break;
                    }
                    getParams["offset"] = subscriptions.list.Count.ToString();
                }
            }

            foreach (Subscriptions.List sub in subscriptions.list.Where(s=>s.username==""))
            {
                ;
            }

                if (subscriptions.list.Any(s=>s.collections.Count()==0))
            {
                ExportUsers("NoList", subscriptions.list.Where(s => s.collections.Count() == 0).Select(s => new { username = s.username, id = s.id }).Distinct().ToDictionary(s=>s.username,s=>s.id));
            //    Console.WriteLine("Users Not on list");
            //    foreach(var subscription in subscriptions.list.Where(s => s.collections.Count() == 0))
            //{
            //        Console.WriteLine(subscription.username);
            //    }
            }

            foreach (Subscriptions.List subscription in subscriptions.ListByExpiration)
            {
                if (!users.ContainsKey(subscription.username))
                {
                    Entities.User? user_info = await GetUserInfo($"/users/{subscription.username}");
                    if (user_info != null && user_info.username != null)
                    {
                        if (user_info.about != null && user_info.about.Contains("/trial/"))
                        {
                            await CheckPromoLink(config, user_info.about);
                        }
                    }

                    DateTime expiration = subscription.subscribedByExpireDate?? DateTime.Today.AddDays(-1);
                    if ((bool)subscription.subscribedIsExpiredNow) expiration = DateTime.Now.AddDays(-7);


                    if (Double.Parse(subscription.subscribePrice) == 0.0) expiration = subscription.subscribedByExpireDate.Value.AddDays(3);


                    users.Add(subscription.username, (subscription.id, expiration.Date,subscription.subscribePrice));
                }
            }
            ignoredUsers.AddRange(subscriptions.list.Where(s => s.collections.Count() > 0 && IgnoredLists.Intersect(s.collections, StringComparer.CurrentCultureIgnoreCase).Count() > 0).Select(x => x.id).ToArray());
            ExportUsers("ignored_"+getParams["type"], ignoredSubscriptions.list.Where(s=>s.collections.Contains("Ignored")).Select(s=>new { username = s.username, id = s.id,price=s.subscribePrice }).Distinct().ToDictionary(s => s.username, s => (s.id,s.price)));
            int[] ignoredIds = ignoredSubscriptions.list.Where(s => s.collections.Contains("Ignored")).Select(s => s.id).ToArray();
            ExportUsers(getParams["type"], users.Where(u=> !ignoredIds.Contains(u.Value.id)).ToDictionary(s => s.Key, s => (s.Value.id, s.Value.price)));

            return users.ToDictionary(s=>s.Key,s=>(s.Value.id,s.Value.expiration));
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }

    public void ExportUsers(string v, Dictionary<string, int> dictionary)
    {
        if (File.Exists(v + ".dat")) File.Delete(v + ".dat");
        File.WriteAllLines(v + ".dat", dictionary.Select(d => "'" + d.Key + "'," + d.Value).ToArray());
    }

    private void ExportUsers(string v, Dictionary<string, (int id,string? price)> dictionary)
    {
        if (File.Exists(v + ".dat")) File.Delete(v + ".dat");
        File.WriteAllLines(v + ".dat", dictionary.Select(d => "'" + d.Key + "'," + d.Value.id+",'"+d.Value.price ?? ""+"'").ToArray());
    }

    public async Task ExportPromosAsync(string v, string promoString)
    {
        //if (File.Exists(v + ".dat")) File.Delete(v + ".dat");
        await File.AppendAllTextAsync(v + ".dat", promoString+Environment.NewLine);
    }

    public void ExportUsers(string v, Dictionary<string, (int id, DateTime expiration,DateTime LastAccessed)> dictionary)
    {
        if (File.Exists(v + ".dat")) File.Delete(v + ".dat");
        File.WriteAllLines(v + ".dat", dictionary.Select(d => "'" + d.Key + "'," + d.Value.id).ToArray());
    }

    private void ExportUsers(string v, List<Subscriptions.List> lists)
    {
        throw new NotImplementedException();
    }

    public async Task<Dictionary<string, (int id, DateTime expiration)>?> GetActiveSubscriptions(string endpoint, bool includeRestricted, IDownloadConfig config)
    {
        Dictionary<string, string> getParams = new()
        {
            { "offset", "0" },
            { "limit", "50" },
            { "type", "active" },
            { "format", "infinite"}
        };

        return await GetAllSubscriptions(getParams, endpoint, includeRestricted, config);
    }


    public async Task<Dictionary<string, (int id, DateTime expiration)>?> GetExpiredSubscriptions(string endpoint, bool includeRestricted, IDownloadConfig config)
    {

        Dictionary<string, string> getParams = new()
        {
            { "offset", "0" },
            { "limit", "50" },
            { "type", "expired" },
            { "format", "infinite"}
        };

        Log.Debug("Calling GetExpiredSubscriptions");

        return await GetAllSubscriptions(getParams, endpoint, includeRestricted, config);
    }


    public async Task<Dictionary<string, int>> GetLists(string endpoint, IDownloadConfig config)
    {
        Log.Debug("Calling GetLists");

        try
        {
            int offset = 0;
            Dictionary<string, string> getParams = new()
            {
                { "offset", offset.ToString() },
                { "skip_users", "all" },
                { "limit", "50" },
                { "format", "infinite" }
            };
            Dictionary<string, int> lists = new();
            while (true)
            {
                string? body = await BuildHeaderAndExecuteRequests(getParams, endpoint, new HttpClient());

                if (body == null)
                {
                    break;
                }

                UserList userList = JsonConvert.DeserializeObject<UserList>(body);
                if (userList == null)
                {
                    break;
                }

                foreach (UserList.List l in userList.list)
                {
                    if (IsStringOnlyDigits(l.id) && !lists.ContainsKey(l.name))
                    {
                        lists.Add(l.name, Convert.ToInt32(l.id));
                    }
                }

                if (userList.hasMore.Value)
                {
                    offset += 50;
                    getParams["offset"] = Convert.ToString(offset);
                }
                else
                {
                    break;
                }

            }
            return lists;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<List<string>?> GetListUsers(string endpoint, IDownloadConfig config)
    {
        Log.Debug($"Calling GetListUsers - {endpoint}");

        try
        {
            int offset = 0;
            Dictionary<string, string> getParams = new()
            {
                { "offset", offset.ToString() },
                { "limit", "50" }
            };
            List<string> users = new();
            Dictionary<string, int> promoUsers = new Dictionary<string, int>();

            while (true)
            {
                var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, new HttpClient());
                if (body == null)
                {
                    break;
                }

                List<UsersList>? usersList = JsonConvert.DeserializeObject<List<UsersList>>(body);

                if (usersList == null || usersList.Count <= 0)
                {
                    break;
                }

                foreach (UsersList ul in usersList)
                {
                    users.Add(ul.username);
                    if ((!ul.subscribedBy.HasValue || (ul.subscribedBy.HasValue && !ul.subscribedBy.Value)) && (ul.canAddSubscriber.HasValue && ul.canAddSubscriber.Value) && (Convert.ToDouble(ul.subscribePrice)==0.0 || ul.promoOffers.Count > 0))
                    {
                        promoUsers.TryAdd(ul.username, ul.id.GetValueOrDefault());
                    }
                }

                if (users.Count < 50)
                {
                    break;
                }

                offset += 50;
                getParams["offset"] = Convert.ToString(offset);

            }

            if(promoUsers.Count>0)
            {
                ExportUsers(DateTime.Now.ToString("yyyyMMddhhmmss") + "_promos", promoUsers);
            }
            return users;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<Dictionary<long, string>> GetMedia(MediaType mediatype,
                                                         string endpoint,
                                                         string? username,
                                                         string folder,
                                                         IDownloadConfig config,
                                                         List<long> paid_post_ids)
    {

        Log.Debug($"Calling GetMedia - {username}");

        try
        {
            Dictionary<long, string> return_urls = new();
            int post_limit = 50;
            int limit = 5;
            int offset = 0;

            Dictionary<string, string> getParams = new();

            switch (mediatype)
            {

                case MediaType.Stories:
                    getParams = new Dictionary<string, string>
                    {
                        { "limit", post_limit.ToString() },
                        { "order", "publish_date_desc" }
                    };
                    break;

                case MediaType.Highlights:
                    getParams = new Dictionary<string, string>
                    {
                        { "limit", limit.ToString() },
                        { "offset", offset.ToString() }
                    };
                    break;
            }

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, new HttpClient());


            if (mediatype == MediaType.Stories)
            {
                Log.Debug("Media Stories - " + endpoint);

                var stories = JsonConvert.DeserializeObject<List<Stories>>(body, m_JsonSerializerSettings) ?? new List<Stories>();
                stories = stories.OrderByDescending(x => x.createdAt).ToList();

                foreach (Stories story in stories)
                {
                    if (story.createdAt != null)
                    {
                        await m_DBHelper.AddStory(folder, story.id, string.Empty, "0", false, false, story.createdAt);
                    }
                    else
                    {
                        await m_DBHelper.AddStory(folder, story.id, string.Empty, "0", false, false, story.media[0].createdAt);
                    }
                    if (story.media != null && story.media.Count > 0)
                    {
                        foreach (Stories.Medium medium in story.media)
                        {
                            await m_DBHelper.AddMedia(folder, medium.id, story.id, medium.files.source.url, null, null, null, "Stories", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), false, false, null);
                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            if (medium.canView && !medium.files.source.url.Contains("upload"))
                            {
                                if (!return_urls.ContainsKey(medium.id))
                                {
                                    return_urls.Add(medium.id, medium.files.source.url);
                                }
                            }
                        }
                    }
                }
            }
            else if (mediatype == MediaType.Highlights)
            {
                List<string> highlight_ids = new();
                var highlights = JsonConvert.DeserializeObject<Highlights>(body, m_JsonSerializerSettings) ?? new Highlights();

                if (highlights.hasMore)
                {
                    offset += 5;
                    getParams["offset"] = offset.ToString();
                    while (true)
                    {
                        Highlights newhighlights = new();

                        Log.Debug("Media Highlights - " + endpoint);

                        var loopbody = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
                        newhighlights = JsonConvert.DeserializeObject<Highlights>(loopbody, m_JsonSerializerSettings);

                        highlights.list.AddRange(newhighlights.list);
                        if (!newhighlights.hasMore)
                        {
                            break;
                        }
                        offset += 5;
                        getParams["offset"] = offset.ToString();
                    }
                }
                foreach (Highlights.List list in highlights.list)
                {
                    if (!highlight_ids.Contains(list.id.ToString()))
                    {
                        highlight_ids.Add(list.id.ToString());
                    }
                }

                foreach (string highlight_id in highlight_ids)
                {
                    HighlightMedia highlightMedia = new();
                    Dictionary<string, string> highlight_headers = GetDynamicHeaders("/api2/v2/stories/highlights/" + highlight_id, string.Empty);

                    HttpClient highlight_client = GetHttpClient(config);

                    HttpRequestMessage highlight_request = new(HttpMethod.Get, $"https://onlyfans.com/api2/v2/stories/highlights/{highlight_id}");

                    foreach (KeyValuePair<string, string> keyValuePair in highlight_headers)
                    {
                        highlight_request.Headers.Add(keyValuePair.Key, keyValuePair.Value);
                    }

                    using var highlightResponse = await highlight_client.SendAsync(highlight_request);
                    highlightResponse.EnsureSuccessStatusCode();
                    var highlightBody = await highlightResponse.Content.ReadAsStringAsync();
                    highlightMedia = JsonConvert.DeserializeObject<HighlightMedia>(highlightBody, m_JsonSerializerSettings);
                    if (highlightMedia != null)
                    {
                        foreach (HighlightMedia.Story item in highlightMedia.stories)
                        {
                            await m_DBHelper.AddStory(folder, item.id, string.Empty, "0", false, false, item.createdAt);
                            if (item.media.Count > 0 && !item.media[0].files.source.url.Contains("upload"))
                            {
                                foreach (HighlightMedia.Medium medium in item.media)
                                {
                                    await m_DBHelper.AddMedia(folder, medium.id, item.id, item.media[0].files.source.url, null, null, null, "Stories", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), false, false, null);
                                    if (medium.type == "photo" && !config.DownloadImages)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "video" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "gif" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "audio" && !config.DownloadAudios)
                                    {
                                        continue;
                                    }
                                    if (!return_urls.ContainsKey(medium.id))
                                    {
                                        return_urls.Add(medium.id, item.media[0].files.source.url);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return return_urls;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<PaidPostCollection> GetPaidPosts(string endpoint, string folder, string username, IDownloadConfig config, List<long> paid_post_ids)
    {
        Log.Debug($"Calling GetPaidPosts - {username}");

        try
        {
            Purchased paidPosts = new();
            PaidPostCollection paidPostCollection = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "publish_date_desc" },
                { "format", "infinite" },
                { "user_id", username }
            };

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
            paidPosts = JsonConvert.DeserializeObject<Purchased>(body, m_JsonSerializerSettings);
            if (paidPosts != null && paidPosts.hasMore)
            {
                getParams["offset"] = paidPosts.list.Count.ToString();
                while (true)
                {

                    Purchased newPaidPosts = new();

                    var loopbody = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
                    newPaidPosts = JsonConvert.DeserializeObject<Purchased>(loopbody, m_JsonSerializerSettings);

                    paidPosts.list.AddRange(newPaidPosts.list);
                    if (!newPaidPosts.hasMore)
                    {
                        break;
                    }
                    getParams["offset"] = Convert.ToString(Convert.ToInt32(getParams["offset"]) + post_limit);
                }

            }
            if (paidPosts != null && paidPosts.list != null)
            {
                foreach (Purchased.List purchase in paidPosts.list)
                {
                    if (purchase.responseType == "post" && purchase.media != null && purchase.media.Count > 0)
                    {
                        List<long> previewids = new();
                        if (purchase.previews != null)
                        {
                            for (int i = 0; i < purchase.previews.Count; i++)
                            {
                                if (!previewids.Contains((long)purchase.previews[i]))
                                {
                                    previewids.Add((long)purchase.previews[i]);
                                }
                            }
                        }
                        else if (purchase.preview != null)
                        {
                            for (int i = 0; i < purchase.preview.Count; i++)
                            {
                                if (!previewids.Contains((long)purchase.preview[i]))
                                {
                                    previewids.Add((long)purchase.preview[i]);
                                }
                            }
                        }
                        await m_DBHelper.AddPost(folder, purchase.id, purchase.text != null ? purchase.text : string.Empty, purchase.price != null ? purchase.price.ToString() : "0", purchase.price != null && purchase.isOpened ? true : false, purchase.isArchived.HasValue ? purchase.isArchived.Value : false, purchase.createdAt != null ? purchase.createdAt.Value : purchase.postedAt.Value);
                        paidPostCollection.PaidPostObjects.Add(purchase);
                        foreach (Messages.Medium medium in purchase.media)
                        {
                            if (!previewids.Contains(medium.id))
                            {
                                paid_post_ids.Add(medium.id);
                            }

                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            if (previewids.Count > 0)
                            {
                                bool has = previewids.Any(cus => cus.Equals(medium.id));
                                if (!has && medium.canView && medium.source != null && medium.source.source != null && !medium.source.source.Contains("upload"))
                                {

                                    if (!paidPostCollection.PaidPosts.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, purchase.id, medium.source.source, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                        paidPostCollection.PaidPosts.Add(medium.id, medium.source.source);
                                        paidPostCollection.PaidPostMedia.Add(medium);
                                    }
                                }
                                else if (!has && medium.canView && medium.files != null && medium.files.drm != null)
                                {

                                    if (!paidPostCollection.PaidPosts.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, purchase.id, medium.files.drm.manifest.dash, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                        paidPostCollection.PaidPosts.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{purchase.id}");
                                        paidPostCollection.PaidPostMedia.Add(medium);
                                    }

                                }
                            }
                            else
                            {
                                if (medium.canView && medium.source != null && medium.source.source != null && !medium.source.source.Contains("upload"))
                                {
                                    if (!paidPostCollection.PaidPosts.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, purchase.id, medium.source.source, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                        paidPostCollection.PaidPosts.Add(medium.id, medium.source.source);
                                        paidPostCollection.PaidPostMedia.Add(medium);
                                    }
                                }
                                else if (medium.canView && medium.files != null && medium.files.drm != null)
                                {
                                    if (!paidPostCollection.PaidPosts.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, purchase.id, medium.files.drm.manifest.dash, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                        paidPostCollection.PaidPosts.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{purchase.id}");
                                        paidPostCollection.PaidPostMedia.Add(medium);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return paidPostCollection;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<PostCollection> GetPosts(string endpoint, string folder, IDownloadConfig config, List<long> paid_post_ids)
    {
        Log.Debug($"Calling GetPosts - {endpoint}");

        try
        {
            Post posts = new();
            PostCollection postCollection = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "publish_date_desc" },
                { "format", "infinite" }
            };

            Enumerations.DownloadDateSelection downloadDateSelection = Enumerations.DownloadDateSelection.before;
            DateTime? downloadAsOf = null;

            if (config.DownloadOnlySpecificDates && config.CustomDate.HasValue)
            {
                downloadDateSelection = config.DownloadDateSelection;
                downloadAsOf = config.CustomDate;
            }
            else if (config.DownloadPostsIncrementally)
            {
                var mostRecentPostDate = await m_DBHelper.GetMostRecentPostDate(new DirectoryInfo(folder).Name);
                if (mostRecentPostDate.HasValue)
                {
                    downloadDateSelection = Enumerations.DownloadDateSelection.after;
                    downloadAsOf = mostRecentPostDate.Value.AddMinutes(-5); // Back track a little for a margin of error
                }
            }

            UpdateGetParamsForDateSelection(
                downloadDateSelection,
                ref getParams,
                downloadAsOf);

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, new HttpClient());
            posts = JsonConvert.DeserializeObject<Post>(body, m_JsonSerializerSettings);
            if (posts != null && posts.hasMore)
            {

                UpdateGetParamsForDateSelection(
                    downloadDateSelection,
                    ref getParams,
                    posts.tailMarker);

                while (true)
                {
                    Post newposts = new();

                    var loopbody = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
                    newposts = JsonConvert.DeserializeObject<Post>(loopbody, m_JsonSerializerSettings);
                    if (newposts == null) break;
                    posts.list.AddRange(newposts.list);
                    if (!newposts.hasMore)
                    {
                        break;
                    }

                    UpdateGetParamsForDateSelection(
                        downloadDateSelection,
                        ref getParams,
                        newposts.tailMarker);
                }
            }
            if (posts == null) posts = new();
            if (posts.list == null) posts.list = new();
            foreach (Post.List post in posts.list)
            {
                if (config.SkipAds)
                {
                    if (post.text != null && post.text.Contains("/trial/"))
                    {
                        await CheckPromoLink(config, post.text);
                        continue;
                    }


                    if (post.rawText != null && post.rawText.Contains("/trial/"))
                    {
                        await CheckPromoLink(config, post.rawText);
                        continue;
                    }
                    if(ignoredUsers.Contains(post.author.id))
                    {
                        continue;
                    }
                    if ((post.rawText != null && ignoredText.Any(t=>post.rawText.Contains(t, StringComparison.CurrentCultureIgnoreCase))) || (post.text != null && ignoredText.Any(t => post.text.Contains(t))))
                    {
                        //|| post.rawText.Contains("/trial/")|| post.text.Contains("/trial/")
                        continue;
                    }
                }
                
                List<long> postPreviewIds = new();
                if (post.preview != null && post.preview.Count > 0)
                {
                    foreach (var id in post.preview)
                    {
                        if (id?.ToString() != "poll")
                        {
                            if (!postPreviewIds.Contains(Convert.ToInt64(id)))
                            {
                                postPreviewIds.Add(Convert.ToInt64(id));
                            }
                        }
                    }
                }
                await m_DBHelper.AddPost(folder, post.id, post.rawText != null ? post.rawText : string.Empty, post.price != null ? post.price.ToString() : "0", post.price != null && post.isOpened ? true : false, post.isArchived, post.postedAt);
                postCollection.PostObjects.Add(post);
                if (post.media != null && post.media.Count > 0)
                {
                    foreach (Post.Medium medium in post.media)
                    {
                        if (medium.type == "photo" && !config.DownloadImages)
                        {
                            continue;
                        }
                        if (medium.type == "video" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "gif" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "audio" && !config.DownloadAudios)
                        {
                            continue;
                        }
                        if (medium.canView && medium.files?.drm == null)
                        {
                            bool has = paid_post_ids.Any(cus => cus.Equals(medium.id));
                            if (medium.source !=null && medium.source.source != null)
                            {
                                if (!has && !medium.source.source.Contains("upload"))
                                {
                                    if (!postCollection.Posts.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, post.id, medium.source.source, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), postPreviewIds.Contains((long)medium.id) ? true : false, false, null);
                                        postCollection.Posts.Add(medium.id, medium.source.source);
                                        postCollection.PostMedia.Add(medium);
                                    }
                                }
                            }
                            else if (medium.preview != null && medium.source.source == null)
                            {
                                if (!has && !medium.preview.Contains("upload"))
                                {
                                    if (!postCollection.Posts.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, post.id, medium.preview, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), postPreviewIds.Contains((long)medium.id) ? true : false, false, null);
                                        postCollection.Posts.Add(medium.id, medium.preview);
                                        postCollection.PostMedia.Add(medium);
                                    }
                                }
                            }
                        }
                        else if (medium.canView && medium.files != null && medium.files.drm != null)
                        {
                            bool has = paid_post_ids.Any(cus => cus.Equals(medium.id));
                            if (!has && medium.files != null && medium.files.drm != null)
                            {
                                if (!postCollection.Posts.ContainsKey(medium.id))
                                {
                                    await m_DBHelper.AddMedia(folder, medium.id, post.id, medium.files.drm.manifest.dash, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), postPreviewIds.Contains((long)medium.id) ? true : false, false, null);
                                    postCollection.Posts.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{post.id}");
                                    postCollection.PostMedia.Add(medium);
                                }
                            }
                        }
                    }
                }
            }

            return postCollection;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }

    public async Task CheckPromoLink(IDownloadConfig config, string txt)
    {
        var re = new System.Text.RegularExpressions.Regex("(https:\\/\\/onlyfans.com\\/action\\/trial\\/([a-zA-Z0-9]{32}))");
        foreach (Match m in re.Matches(txt))
        {
            if (!promoLinks.Contains(m.Groups[2].Value))
            {
                Console.WriteLine("'{0}' found at index {1}.", m.Value, m.Index);
                var trialParams = new Dictionary<string, string>();
                trialParams.Add("code", m.Groups[2].Value);
                promoLinks.Add(m.Groups[2].Value);
                var checkBody = await BuildHeaderAndExecutePosts(trialParams, "/trials/check", GetHttpClient(config));
                
                try
                {
                    dynamic tBody = JsonConvert.DeserializeObject(checkBody);
                    var tstring = string.Format("{0},'{1}','{2}',{3}", tBody.user.id, tBody.user.username, m.Value,tBody.user.subscribePrice ?? 0);
                    await ExportPromosAsync("Promos_" + DateTime.Today.ToString("yyyyMMdd"), tstring);
                    // do stuff with x
                }
                catch (Exception e)
                {
                    ;
                }
            }

        }
    }

    public async Task<SinglePostCollection> GetPost(string endpoint, string folder, IDownloadConfig config)
    {
        Log.Debug($"Calling GetPost - {endpoint}");

        try
        {
            SinglePost singlePost = new();
            SinglePostCollection singlePostCollection = new();
            Dictionary<string, string> getParams = new()
            {
                { "skip_users", "all" }
            };

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, new HttpClient());
            singlePost = JsonConvert.DeserializeObject<SinglePost>(body, m_JsonSerializerSettings);

            if (singlePost != null)
            {
                List<long> postPreviewIds = new();
                if (singlePost.preview != null && singlePost.preview.Count > 0)
                {
                    foreach (var id in singlePost.preview)
                    {
                        if (id?.ToString() != "poll")
                        {
                            if (!postPreviewIds.Contains(Convert.ToInt64(id)))
                            {
                                postPreviewIds.Add(Convert.ToInt64(id));
                            }
                        }
                    }
                }
                await m_DBHelper.AddPost(folder, singlePost.id, singlePost.text != null ? singlePost.text : string.Empty, singlePost.price != null ? singlePost.price.ToString() : "0", singlePost.price != null && singlePost.isOpened ? true : false, singlePost.isArchived, singlePost.postedAt);
                singlePostCollection.SinglePostObjects.Add(singlePost);
                if (singlePost.media != null && singlePost.media.Count > 0)
                {
                    foreach (SinglePost.Medium medium in singlePost.media)
                    {
                        if (medium.type == "photo" && !config.DownloadImages)
                        {
                            continue;
                        }
                        if (medium.type == "video" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "gif" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "audio" && !config.DownloadAudios)
                        {
                            continue;
                        }
                        if (medium.canView && medium.files?.drm == null)
                        {
                            if (medium.source.source != null)
                            {
                                if (!medium.source.source.Contains("upload"))
                                {
                                    if (!singlePostCollection.SinglePosts.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, singlePost.id, medium.source.source, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), postPreviewIds.Contains((long)medium.id) ? true : false, false, null);
                                        singlePostCollection.SinglePosts.Add(medium.id, medium.source.source);
                                        singlePostCollection.SinglePostMedia.Add(medium);
                                    }
                                }
                            }
                            else if (medium.preview != null && medium.source.source == null)
                            {
                                if (!medium.preview.Contains("upload"))
                                {
                                    if (!singlePostCollection.SinglePosts.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, singlePost.id, medium.preview, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), postPreviewIds.Contains((long)medium.id) ? true : false, false, null);
                                        singlePostCollection.SinglePosts.Add(medium.id, medium.preview);
                                        singlePostCollection.SinglePostMedia.Add(medium);
                                    }
                                }
                            }
                        }
                        else if (medium.canView && medium.files != null && medium.files.drm != null)
                        {
                            if (medium.files != null && medium.files.drm != null)
                            {
                                if (!singlePostCollection.SinglePosts.ContainsKey(medium.id))
                                {
                                    await m_DBHelper.AddMedia(folder, medium.id, singlePost.id, medium.files.drm.manifest.dash, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), postPreviewIds.Contains((long)medium.id) ? true : false, false, null);
                                    singlePostCollection.SinglePosts.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{singlePost.id}");
                                    singlePostCollection.SinglePostMedia.Add(medium);
                                }
                            }
                        }
                    }
                }
            }

            return singlePostCollection;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }

    public async Task<StreamsCollection> GetStreams(string endpoint, string folder, IDownloadConfig config, List<long> paid_post_ids)
    {
        Log.Debug($"Calling GetStreams - {endpoint}");

        try
        {
            Streams streams = new();
            StreamsCollection streamsCollection = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "publish_date_desc" },
                { "format", "infinite" }
            };

            Enumerations.DownloadDateSelection downloadDateSelection = Enumerations.DownloadDateSelection.before;
            if (config.DownloadOnlySpecificDates && config.CustomDate.HasValue)
            {
                downloadDateSelection = config.DownloadDateSelection;
            }

            UpdateGetParamsForDateSelection(
                downloadDateSelection,
                ref getParams,
                config.CustomDate);

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, new HttpClient());
            streams = JsonConvert.DeserializeObject<Streams>(body, m_JsonSerializerSettings);
            if (streams != null && streams.hasMore)
            {

                UpdateGetParamsForDateSelection(
                    downloadDateSelection,
                    ref getParams,
                    streams.tailMarker);

                while (true)
                {
                    Streams newstreams = new();

                    var loopbody = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
                    newstreams = JsonConvert.DeserializeObject<Streams>(loopbody, m_JsonSerializerSettings);

                    streams.list.AddRange(newstreams.list);
                    if (!newstreams.hasMore)
                    {
                        break;
                    }

                    UpdateGetParamsForDateSelection(
                        downloadDateSelection,
                        ref getParams,
                        newstreams.tailMarker);
                }
            }

            foreach (Streams.List stream in streams.list)
            {
                List<long> streamPreviewIds = new();
                if (stream.preview != null && stream.preview.Count > 0)
                {
                    foreach (var id in stream.preview)
                    {
                        if (id?.ToString() != "poll")
                        {
                            if (!streamPreviewIds.Contains(Convert.ToInt64(id)))
                            {
                                streamPreviewIds.Add(Convert.ToInt64(id));
                            }
                        }
                    }
                }
                await m_DBHelper.AddPost(folder, stream.id, stream.text != null ? stream.text : string.Empty, stream.price != null ? stream.price.ToString() : "0", stream.price != null && stream.isOpened ? true : false, stream.isArchived, stream.postedAt);
                streamsCollection.StreamObjects.Add(stream);
                if (stream.media != null && stream.media.Count > 0)
                {
                    foreach (Streams.Medium medium in stream.media)
                    {
                        if (medium.type == "photo" && !config.DownloadImages)
                        {
                            continue;
                        }
                        if (medium.type == "video" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "gif" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "audio" && !config.DownloadAudios)
                        {
                            continue;
                        }
                        if (medium.canView && medium.files?.drm == null)
                        {
                            bool has = paid_post_ids.Any(cus => cus.Equals(medium.id));
                            if (!has && !medium.source.source.Contains("upload"))
                            {
                                if (!streamsCollection.Streams.ContainsKey(medium.id))
                                {
                                    await m_DBHelper.AddMedia(folder, medium.id, stream.id, medium.source.source, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), streamPreviewIds.Contains((long)medium.id) ? true : false, false, null);
                                    streamsCollection.Streams.Add(medium.id, medium.source.source);
                                    streamsCollection.StreamMedia.Add(medium);
                                }
                            }
                        }
                        else if (medium.canView && medium.files != null && medium.files.drm != null)
                        {
                            bool has = paid_post_ids.Any(cus => cus.Equals(medium.id));
                            if (!has && medium.files != null && medium.files.drm != null)
                            {
                                if (!streamsCollection.Streams.ContainsKey(medium.id))
                                {
                                    await m_DBHelper.AddMedia(folder, medium.id, stream.id, medium.files.drm.manifest.dash, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), streamPreviewIds.Contains((long)medium.id) ? true : false, false, null);
                                    streamsCollection.Streams.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{stream.id}");
                                    streamsCollection.StreamMedia.Add(medium);
                                }
                            }
                        }
                    }
                }
            }

            return streamsCollection;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<ArchivedCollection> GetArchived(string endpoint, string folder, IDownloadConfig config)
    {
        Log.Debug($"Calling GetArchived - {endpoint}");

        try
        {
            Archived archived = new();
            ArchivedCollection archivedCollection = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "publish_date_desc" },
                { "skip_users", "all" },
                { "format", "infinite" },
                { "label", "archived" },
                { "counters", "1" }
            };

            Enumerations.DownloadDateSelection downloadDateSelection = Enumerations.DownloadDateSelection.before;
            if (config.DownloadOnlySpecificDates && config.CustomDate.HasValue)
            {
                downloadDateSelection = config.DownloadDateSelection;
            }

            UpdateGetParamsForDateSelection(
                downloadDateSelection,
                ref getParams,
                config.CustomDate);

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
            archived = JsonConvert.DeserializeObject<Archived>(body, m_JsonSerializerSettings);
            if (archived != null && archived.hasMore)
            {
                UpdateGetParamsForDateSelection(
                   downloadDateSelection,
                   ref getParams,
                   archived.tailMarker);
                while (true)
                {
                    Archived newarchived = new();

                    var loopbody = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
                    newarchived = JsonConvert.DeserializeObject<Archived>(loopbody, m_JsonSerializerSettings);

                    
                    if (newarchived==null || !newarchived.hasMore)
                    {
                        break;
                    }
                    archived.list.AddRange(newarchived.list);
                    UpdateGetParamsForDateSelection(
                       downloadDateSelection,
                       ref getParams,
                       newarchived.tailMarker);
                }
            }

            if (archived == null) archived = new();
            if (archived.list == null) archived.list = new();
            foreach (Archived.List archive in archived.list)
            {
                if(ignoredUsers.Contains(archive.author.id))
                {
                    continue;
                }
                List<long> previewids = new();
                if (archive.preview != null)
                {
                    for (int i = 0; i < archive.preview.Count; i++)
                    {
                        if (archive.preview[i]?.ToString() != "poll")
                        {
                            if (!previewids.Contains((long)archive.preview[i]))
                            {
                                previewids.Add((long)archive.preview[i]);
                            }
                        }
                    }
                }
                await m_DBHelper.AddPost(folder, archive.id, archive.text != null ? archive.text : string.Empty, archive.price != null ? archive.price.ToString() : "0", archive.price != null && archive.isOpened ? true : false, archive.isArchived, archive.postedAt);
                archivedCollection.ArchivedPostObjects.Add(archive);
                if (archive.media != null && archive.media.Count > 0)
                {
                    foreach (Archived.Medium medium in archive.media)
                    {
                        if (medium.type == "photo" && !config.DownloadImages)
                        {
                            continue;
                        }
                        if (medium.type == "video" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "gif" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "audio" && !config.DownloadAudios)
                        {
                            continue;
                        }
                        if (medium.canView && medium.files?.drm == null && medium.source !=null && !medium.source.source.Contains("upload"))
                        {
                            if (!archivedCollection.ArchivedPosts.ContainsKey(medium.id))
                            {
                                await m_DBHelper.AddMedia(folder, medium.id, archive.id, medium.source.source, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                archivedCollection.ArchivedPosts.Add(medium.id, medium.source.source);
                                archivedCollection.ArchivedPostMedia.Add(medium);
                            }
                        }
                        else if (medium.canView && medium.files != null && medium.files.drm != null)
                        {
                            if (!archivedCollection.ArchivedPosts.ContainsKey(medium.id))
                            {
                                await m_DBHelper.AddMedia(folder, medium.id, archive.id, medium.files.drm.manifest.dash, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                archivedCollection.ArchivedPosts.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{archive.id}");
                                archivedCollection.ArchivedPostMedia.Add(medium);
                            }
                        }
                    }
                }
            }

            return archivedCollection;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<MessageCollection> GetMessages(string endpoint, string folder, IDownloadConfig config)
    {
        Log.Debug($"Calling GetMessages - {endpoint}");

        try
        {
            Messages messages = new();
            MessageCollection messageCollection = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                //{ "order", "asc" }
            };

             if (config.DownloadPostsIncrementally)
            {
                var mostRecentPostDate = await m_DBHelper.GetMostRecentMessageId(new DirectoryInfo(folder).Name);
                if (mostRecentPostDate !=null)
                {
                    getParams["id"] = mostRecentPostDate;
                    getParams["order"]="asc";
                }
            }

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
            messages = JsonConvert.DeserializeObject<Messages>(body, m_JsonSerializerSettings);
            if (messages != null && messages.hasMore)
            {
                getParams["id"] = messages.list[^1].id.ToString();
                while (true)
                {
                    Messages newmessages = new();

                    var loopbody = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
                    newmessages = JsonConvert.DeserializeObject<Messages>(loopbody, m_JsonSerializerSettings);
                    if(newmessages?.list!=null) messages.list.AddRange(newmessages.list);
                    if (newmessages==null || !newmessages.hasMore)
                    {
                        break;
                    }
                    getParams["id"] = newmessages.list[newmessages.list.Count - 1].id.ToString();
                }
            }
            if(messages !=null) {
            foreach (Messages.List list in messages.list)
            {
                if (config.SkipAds)
                {
                    
                    if (list.text != null && list.text.Contains("/trial/"))
                    {
                        await CheckPromoLink(config, list.text);
                        continue;
                    }

                    if (list.text != null && ignoredText.Any(t => list.text.Contains(t,StringComparison.CurrentCultureIgnoreCase)))
                        {
                        continue;
                    }
                }
                if (ignoredUsers.Contains((int)list.fromUser.id))
                {
                    continue;
                }
                List<long> messagePreviewIds = new();
                if (list.previews != null && list.previews.Count > 0)
                {
                    foreach (var id in list.previews)
                    {
                        if (!messagePreviewIds.Contains((long)id))
                        {
                            messagePreviewIds.Add((long)id);
                        }
                    }
                }
                await m_DBHelper.AddMessage(folder, list.id, list.text != null ? list.text : string.Empty, list.price != null ? list.price.ToString() : "0", list.canPurchaseReason == "opened" ? true : list.canPurchaseReason != "opened" ? false : (bool?)null ?? false, false, list.createdAt.HasValue ? list.createdAt.Value : DateTime.Now, list.fromUser != null && list.fromUser.id != null ? list.fromUser.id.Value : int.MinValue);
                messageCollection.MessageObjects.Add(list);
                if (list.canPurchaseReason != "opened" && list.media != null && list.media.Count > 0)
                {
                    foreach (Messages.Medium medium in list.media)
                    {
                        if (medium.canView && medium.source!=null && medium.source.source != null && !medium.source.source.Contains("upload"))
                        {
                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            if (!messageCollection.Messages.ContainsKey(medium.id))
                            {
                                await m_DBHelper.AddMedia(folder, medium.id, list.id, medium.source.source, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), messagePreviewIds.Contains(medium.id) ? true : false, false, null);
                                messageCollection.Messages.Add(medium.id, medium.source.source.ToString());
                                messageCollection.MessageMedia.Add(medium);
                            }
                        }
                        else if (medium.canView && medium.files != null && medium.files.drm != null)
                        {
                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            if (!messageCollection.Messages.ContainsKey(medium.id))
                            {
                                await m_DBHelper.AddMedia(folder, medium.id, list.id, medium.files.drm.manifest.dash, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), messagePreviewIds.Contains(medium.id) ? true : false, false, null);
                                messageCollection.Messages.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{list.id}");
                                messageCollection.MessageMedia.Add(medium);
                            }
                        }
                    }
                }
                else if (messagePreviewIds.Count > 0)
                {
                    foreach (Messages.Medium medium in list.media)
                    {
                        if (medium.canView && medium.source !=null && medium.source.source != null && !medium.source.source.Contains("upload") && messagePreviewIds.Contains(medium.id))
                        {
                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            if (!messageCollection.Messages.ContainsKey(medium.id))
                            {
                                await m_DBHelper.AddMedia(folder, medium.id, list.id, medium.source.source, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), messagePreviewIds.Contains(medium.id) ? true : false, false, null);
                                messageCollection.Messages.Add(medium.id, medium.source.source.ToString());
                                messageCollection.MessageMedia.Add(medium);
                            }
                        }
                        else if (medium.canView && medium.files != null && medium.files.drm != null && messagePreviewIds.Contains(medium.id))
                        {
                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            if (!messageCollection.Messages.ContainsKey(medium.id))
                            {
                                await m_DBHelper.AddMedia(folder, medium.id, list.id, medium.files.drm.manifest.dash, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), messagePreviewIds.Contains(medium.id) ? true : false, false, null);
                                messageCollection.Messages.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{list.id}");
                                messageCollection.MessageMedia.Add(medium);
                            }
                        }
                    }
                }
            }
            }
            return messageCollection;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }

    public async Task<PaidMessageCollection> GetPaidMessage(string endpoint, string folder, IDownloadConfig config)
    {
        Log.Debug($"Calling GetPaidMessage - {endpoint}");

        try
        {
            SingleMessage message = new();
            PaidMessageCollection paidMessageCollection = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "desc" }
            };

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
            message = JsonConvert.DeserializeObject<SingleMessage>(body, m_JsonSerializerSettings);

            await m_DBHelper.AddMessage(folder, message.id, message.text != null ? message.text : string.Empty, message.price != null ? message.price.ToString() : "0", true, false, message.createdAt.HasValue ? message.createdAt.Value : DateTime.Now, message.fromUser != null && message.fromUser.id != null ? message.fromUser.id.Value : int.MinValue);

            List<long> messagePreviewIds = new();
            if (message.previews != null && message.previews.Count > 0)
            {
                foreach (var id in message.previews)
                {
                    if (!messagePreviewIds.Contains((long)id))
                    {
                        messagePreviewIds.Add((long)id);
                    }
                }
            }

            if (message.media != null && message.media.Count > 0)
                {
                    foreach (Messages.Medium medium in message.media)
                    {
                        if (medium.canView && medium.source.source != null && !medium.source.source.Contains("upload"))
                        {
                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            
                            if (!paidMessageCollection.PaidMessages.ContainsKey(medium.id))
                            {
                                await m_DBHelper.AddMedia(folder, medium.id, message.id, medium.source.source, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), messagePreviewIds.Contains(medium.id) ? true : false, false, null);
                                paidMessageCollection.PaidMessages.Add(medium.id, medium.source.source.ToString());
                                paidMessageCollection.PaidMessageMedia.Add(medium);
                            }
                        }
                        else if (medium.canView && medium.files != null && medium.files.drm != null)
                        {
                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            
                            if (!paidMessageCollection.PaidMessages.ContainsKey(medium.id))
                            {
                               await m_DBHelper.AddMedia(folder, medium.id, message.id, medium.videoSources._720, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), messagePreviewIds.Contains(medium.id) ? true : false, false, null);
                               paidMessageCollection.PaidMessages.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{message.id}");
                               paidMessageCollection.PaidMessageMedia.Add(medium);
                            }
                        }
                    }
                }
                else if (messagePreviewIds.Count > 0)
                {
                    foreach(Messages.Medium medium in message.media)
                    {
                        if (medium.canView && medium.source.source != null && !medium.source.source.Contains("upload") && messagePreviewIds.Contains(medium.id))
                        {
                            if (medium.type == "photo" && !config.DownloadImages)
                            {
                                continue;
                            }
                            if (medium.type == "video" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "gif" && !config.DownloadVideos)
                            {
                                continue;
                            }
                            if (medium.type == "audio" && !config.DownloadAudios)
                            {
                                continue;
                            }
                            if (!paidMessageCollection.PaidMessages.ContainsKey(medium.id))
                            {
                                await m_DBHelper.AddMedia(folder, medium.id, message.id, medium.source.source, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), messagePreviewIds.Contains(medium.id) ? true : false, false, null);
                                paidMessageCollection.PaidMessages.Add(medium.id, medium.source.source.ToString());
                                paidMessageCollection.PaidMessageMedia.Add(medium);
                        }
                    }
                    else if (medium.canView && medium.files != null && medium.files.drm != null && messagePreviewIds.Contains(medium.id))
                    {
                        if (medium.type == "photo" && !config.DownloadImages)
                        {
                            continue;
                        }
                        if (medium.type == "video" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "gif" && !config.DownloadVideos)
                        {
                            continue;
                        }
                        if (medium.type == "audio" && !config.DownloadAudios)
                        {
                            continue;
                        }
                        if (!paidMessageCollection.PaidMessages.ContainsKey(medium.id))
                        {
                            await m_DBHelper.AddMedia(folder, medium.id, message.id, medium.files.drm.manifest.dash, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), messagePreviewIds.Contains(medium.id) ? true : false, false, null);
                            paidMessageCollection.PaidMessages.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{message.id}");
                            paidMessageCollection.PaidMessageMedia.Add(medium);
                        }
                    }
                    }
                }            

            return paidMessageCollection;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<PaidMessageCollection> GetPaidMessages(string endpoint, string folder, string username, IDownloadConfig config)
    {
        Log.Debug($"Calling GetPaidMessages - {username}");

        try
        {
            Purchased paidMessages = new();
            PaidMessageCollection paidMessageCollection = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "publish_date_desc" },
                { "format", "infinite" },
                { "user_id", username }
            };

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
            paidMessages = JsonConvert.DeserializeObject<Purchased>(body, m_JsonSerializerSettings);
            if (paidMessages != null && paidMessages.hasMore)
            {
                getParams["offset"] = paidMessages.list.Count.ToString();
                while (true)
                {
                    string loopqueryParams = "?" + string.Join("&", getParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                    Purchased newpaidMessages = new();
                    Dictionary<string, string> loopheaders = GetDynamicHeaders("/api2/v2" + endpoint, loopqueryParams);
                    HttpClient loopclient = GetHttpClient(config);

                    HttpRequestMessage looprequest = new(HttpMethod.Get, $"{Constants.API_URL}{endpoint}{loopqueryParams}");

                    foreach (KeyValuePair<string, string> keyValuePair in loopheaders)
                    {
                        looprequest.Headers.Add(keyValuePair.Key, keyValuePair.Value);
                    }
                    using (var loopresponse = await loopclient.SendAsync(looprequest))
                    {
                        loopresponse.EnsureSuccessStatusCode();
                        var loopbody = await loopresponse.Content.ReadAsStringAsync();
                        newpaidMessages = JsonConvert.DeserializeObject<Purchased>(loopbody, m_JsonSerializerSettings);
                    }
                    paidMessages.list.AddRange(newpaidMessages.list);
                    if (!newpaidMessages.hasMore)
                    {
                        break;
                    }
                    getParams["offset"] = Convert.ToString(Convert.ToInt32(getParams["offset"]) + post_limit);
                }
            }

            if (paidMessages != null && paidMessages.list != null && paidMessages.list.Count > 0)
            {
                foreach (Purchased.List purchase in paidMessages.list.Where(p => p.responseType == "message").OrderByDescending(p => p.postedAt ?? p.createdAt))
                {
                    if (purchase.postedAt != null)
                    {
                        await m_DBHelper.AddMessage(folder, purchase.id, purchase.text != null ? purchase.text : string.Empty, purchase.price != null ? purchase.price : "0", true, false, purchase.postedAt.Value, purchase.fromUser.id);
                    }
                    else
                    {
                        await m_DBHelper.AddMessage(folder, purchase.id, purchase.text != null ? purchase.text : string.Empty, purchase.price != null ? purchase.price : "0", true, false, purchase.createdAt.Value, purchase.fromUser.id);
                    }
                    paidMessageCollection.PaidMessageObjects.Add(purchase);
                    if (purchase.media != null && purchase.media.Count > 0)
                    {
                        List<long> previewids = new();
                        if (purchase.previews != null)
                        {
                            for (int i = 0; i < purchase.previews.Count; i++)
                            {
                                if (!previewids.Contains((long)purchase.previews[i]))
                                {
                                    previewids.Add((long)purchase.previews[i]);
                                }
                            }
                        }
                        else if (purchase.preview != null)
                        {
                            for (int i = 0; i < purchase.preview.Count; i++)
                            {
                                if (!previewids.Contains((long)purchase.preview[i]))
                                {
                                    previewids.Add((long)purchase.preview[i]);
                                }
                            }
                        }

                        foreach (Messages.Medium medium in purchase.media)
                        {
                            if (previewids.Count > 0)
                            {
                                bool has = previewids.Any(cus => cus.Equals(medium.id));
                                if (!has && medium.canView && medium.source != null && medium.source.source != null && !medium.source.source.Contains("upload"))
                                {
                                    if (medium.type == "photo" && !config.DownloadImages)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "video" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "gif" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "audio" && !config.DownloadAudios)
                                    {
                                        continue;
                                    }
                                    if (!paidMessageCollection.PaidMessages.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, purchase.id, medium.source.source, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                        paidMessageCollection.PaidMessages.Add(medium.id, medium.source.source);
                                        paidMessageCollection.PaidMessageMedia.Add(medium);
                                    }
                                }
                                else if (!has && medium.canView && medium.files != null && medium.files.drm != null)
                                {
                                    if (medium.type == "photo" && !config.DownloadImages)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "video" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "gif" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "audio" && !config.DownloadAudios)
                                    {
                                        continue;
                                    }
                                    if (!paidMessageCollection.PaidMessages.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, purchase.id, medium.files.drm.manifest.dash, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                        paidMessageCollection.PaidMessages.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{purchase.id}");
                                        paidMessageCollection.PaidMessageMedia.Add(medium);
                                    }
                                }
                            }
                            else
                            {
                                if (medium.canView && medium.source != null && medium.source.source != null && !medium.source.source.Contains("upload"))
                                {
                                    if (medium.type == "photo" && !config.DownloadImages)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "video" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "gif" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "audio" && !config.DownloadAudios)
                                    {
                                        continue;
                                    }
                                    if (!paidMessageCollection.PaidMessages.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, purchase.id, medium.source.source, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                        paidMessageCollection.PaidMessages.Add(medium.id, medium.source.source);
                                        paidMessageCollection.PaidMessageMedia.Add(medium);
                                    }
                                }
                                else if (medium.canView && medium.files != null && medium.files.drm != null)
                                {
                                    if (medium.type == "photo" && !config.DownloadImages)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "video" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "gif" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "audio" && !config.DownloadAudios)
                                    {
                                        continue;
                                    }
                                    if (!paidMessageCollection.PaidMessages.ContainsKey(medium.id))
                                    {
                                        await m_DBHelper.AddMedia(folder, medium.id, purchase.id, medium.files.drm.manifest.dash, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                        paidMessageCollection.PaidMessages.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{purchase.id}");
                                        paidMessageCollection.PaidMessageMedia.Add(medium);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return paidMessageCollection;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }

    public async Task<Dictionary<string, int>> GetPurchasedTabUsers(string endpoint, IDownloadConfig config, Dictionary<string, int> users)
    {
        Log.Debug($"Calling GetPurchasedTabUsers - {endpoint}");

        try
        {
            Dictionary<string, int> purchasedTabUsers = new();
            Purchased purchased = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "publish_date_desc" },
                { "format", "infinite" }
            };

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
            purchased = JsonConvert.DeserializeObject<Purchased>(body, m_JsonSerializerSettings);
            if (purchased != null && purchased.hasMore)
            {
                getParams["offset"] = purchased.list.Count.ToString();
                while (true)
                {
                    string loopqueryParams = "?" + string.Join("&", getParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                    Purchased newPurchased = new();
                    Dictionary<string, string> loopheaders = GetDynamicHeaders("/api2/v2" + endpoint, loopqueryParams);
                    HttpClient loopclient = GetHttpClient(config);

                    HttpRequestMessage looprequest = new(HttpMethod.Get, $"{Constants.API_URL}{endpoint}{loopqueryParams}");

                    foreach (KeyValuePair<string, string> keyValuePair in loopheaders)
                    {
                        looprequest.Headers.Add(keyValuePair.Key, keyValuePair.Value);
                    }
                    using (var loopresponse = await loopclient.SendAsync(looprequest))
                    {
                        loopresponse.EnsureSuccessStatusCode();
                        var loopbody = await loopresponse.Content.ReadAsStringAsync();
                        newPurchased = JsonConvert.DeserializeObject<Purchased>(loopbody, m_JsonSerializerSettings);
                    }
                    purchased.list.AddRange(newPurchased.list);
                    if (!newPurchased.hasMore)
                    {
                        break;
                    }
                    getParams["offset"] = Convert.ToString(Convert.ToInt32(getParams["offset"]) + post_limit);
                }
            }

            if (purchased.list != null && purchased.list.Count > 0)
            {
                foreach (Purchased.List purchase in purchased.list.OrderByDescending(p => p.postedAt ?? p.createdAt))
                {
                    if (purchase.fromUser != null)
                    {
                        if (users.Values.Contains(purchase.fromUser.id))
                        {
                            if (!string.IsNullOrEmpty(users.FirstOrDefault(x => x.Value == purchase.fromUser.id).Key))
                            {
                                if (!purchasedTabUsers.ContainsKey(users.FirstOrDefault(x => x.Value == purchase.fromUser.id).Key))
                                {
                                    purchasedTabUsers.Add(users.FirstOrDefault(x => x.Value == purchase.fromUser.id).Key, purchase.fromUser.id);
                                }
                            }
                            else
                            {
                                if (!purchasedTabUsers.ContainsKey($"Deleted User - {purchase.fromUser.id}"))
                                {
                                    purchasedTabUsers.Add($"Deleted User - {purchase.fromUser.id}", purchase.fromUser.id);
                                }
                            }
                        }
                        else
                        {
                            JObject user = await GetUserInfoById($"/users/list?x[]={purchase.fromUser.id}");

                            if(user is null)
                            {
                                if (!config.BypassContentForCreatorsWhoNoLongerExist)
                                {
                                    purchasedTabUsers.Add($"Deleted User - {purchase.fromUser.id}", purchase.fromUser.id);
                                }
                                Log.Debug("Content creator not longer exists - {0}", purchase.fromUser.id);
                            }
                            else if (!string.IsNullOrEmpty(user[purchase.fromUser.id.ToString()]["username"].ToString()))
                            {
                                if (!purchasedTabUsers.ContainsKey(user[purchase.fromUser.id.ToString()]["username"].ToString()))
                                {
                                    purchasedTabUsers.Add(user[purchase.fromUser.id.ToString()]["username"].ToString(), purchase.fromUser.id);
                                }
                            }
                            else
                            {
                                if (!purchasedTabUsers.ContainsKey($"Deleted User - {purchase.fromUser.id}"))
                                {
                                    purchasedTabUsers.Add($"Deleted User - {purchase.fromUser.id}", purchase.fromUser.id);
                                }
                            }
                        }
                    }
                    else if (purchase.author != null)
                    {
                        if (users.Values.Contains(purchase.author.id))
                        {
                            if (!string.IsNullOrEmpty(users.FirstOrDefault(x => x.Value == purchase.author.id).Key))
                            {
                                if (!purchasedTabUsers.ContainsKey(users.FirstOrDefault(x => x.Value == purchase.author.id).Key) && users.ContainsKey(users.FirstOrDefault(x => x.Value == purchase.author.id).Key))
                                {
                                    purchasedTabUsers.Add(users.FirstOrDefault(x => x.Value == purchase.author.id).Key, purchase.author.id);
                                }
                            }
                            else
                            {
                                if (!purchasedTabUsers.ContainsKey($"Deleted User - {purchase.author.id}"))
                                {
                                    purchasedTabUsers.Add($"Deleted User - {purchase.author.id}", purchase.author.id);
                                }
                            }
                        }
                        else
                        {
                            JObject user = await GetUserInfoById($"/users/list?x[]={purchase.author.id}");

                            if (user is null)
                            {
                                if (!config.BypassContentForCreatorsWhoNoLongerExist)
                                {
                                    purchasedTabUsers.Add($"Deleted User - {purchase.fromUser.id}", purchase.fromUser.id);
                                }
                                Log.Debug("Content creator not longer exists - {0}", purchase.fromUser.id);
                            }
                            else if (!string.IsNullOrEmpty(user[purchase.author.id.ToString()]["username"].ToString()))
                            {
                                if (!purchasedTabUsers.ContainsKey(user[purchase.author.id.ToString()]["username"].ToString()) && users.ContainsKey(user[purchase.author.id.ToString()]["username"].ToString()))
                                {
                                    purchasedTabUsers.Add(user[purchase.author.id.ToString()]["username"].ToString(), purchase.author.id);
                                }
                            }
                            else
                            {
                                if (!purchasedTabUsers.ContainsKey($"Deleted User - {purchase.author.id}"))
                                {
                                    purchasedTabUsers.Add($"Deleted User - {purchase.author.id}", purchase.author.id);
                                }
                            }
                        }
                    }
                }
            }

            return purchasedTabUsers;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }

    public async Task<List<PurchasedTabCollection>> GetPurchasedTab(string endpoint, string folder, IDownloadConfig config, Dictionary<string, int> users)
    {
        Log.Debug($"Calling GetPurchasedTab - {endpoint}");

        try
        {
            Dictionary<long, List<Purchased.List>> userPurchases = new Dictionary<long, List<Purchased.List>>();
            List<PurchasedTabCollection> purchasedTabCollections = new();
            Purchased purchased = new();
            int post_limit = 50;
            Dictionary<string, string> getParams = new()
            {
                { "limit", post_limit.ToString() },
                { "order", "publish_date_desc" },
                { "format", "infinite" }
            };

            var body = await BuildHeaderAndExecuteRequests(getParams, endpoint, GetHttpClient(config));
            purchased = JsonConvert.DeserializeObject<Purchased>(body, m_JsonSerializerSettings);
            if (purchased != null && purchased.hasMore)
            {
                getParams["offset"] = purchased.list.Count.ToString();
                while (true)
                {
                    string loopqueryParams = "?" + string.Join("&", getParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                    Purchased newPurchased = new();
                    Dictionary<string, string> loopheaders = GetDynamicHeaders("/api2/v2" + endpoint, loopqueryParams);
                    HttpClient loopclient = GetHttpClient(config);

                    HttpRequestMessage looprequest = new(HttpMethod.Get, $"{Constants.API_URL}{endpoint}{loopqueryParams}");

                    foreach (KeyValuePair<string, string> keyValuePair in loopheaders)
                    {
                        looprequest.Headers.Add(keyValuePair.Key, keyValuePair.Value);
                    }
                    using (var loopresponse = await loopclient.SendAsync(looprequest))
                    {
                        loopresponse.EnsureSuccessStatusCode();
                        var loopbody = await loopresponse.Content.ReadAsStringAsync();
                        newPurchased = JsonConvert.DeserializeObject<Purchased>(loopbody, m_JsonSerializerSettings);
                    }
                    purchased.list.AddRange(newPurchased.list);
                    if (!newPurchased.hasMore)
                    {
                        break;
                    }
                    getParams["offset"] = Convert.ToString(Convert.ToInt32(getParams["offset"]) + post_limit);
                }
            }

            if (purchased.list != null && purchased.list.Count > 0)
            {
                foreach (Purchased.List purchase in purchased.list.OrderByDescending(p => p.postedAt ?? p.createdAt))
                {
                    if (purchase.fromUser != null)
                    {
                        if (!userPurchases.ContainsKey(purchase.fromUser.id))
                        {
                            userPurchases.Add(purchase.fromUser.id, new List<Purchased.List>());
                        }
                        userPurchases[purchase.fromUser.id].Add(purchase);
                    }
                    else if (purchase.author != null)
                    {
                        if (!userPurchases.ContainsKey(purchase.author.id))
                        {
                            userPurchases.Add(purchase.author.id, new List<Purchased.List>());
                        }
                        userPurchases[purchase.author.id].Add(purchase);
                    }
                }
            }

            foreach (KeyValuePair<long, List<Purchased.List>> user in userPurchases)
            {
                PurchasedTabCollection purchasedTabCollection = new PurchasedTabCollection();
                JObject userObject = await GetUserInfoById($"/users/list?x[]={user.Key}");
                purchasedTabCollection.UserId = user.Key;
                purchasedTabCollection.Username = userObject is not null && !string.IsNullOrEmpty(userObject[user.Key.ToString()]["username"].ToString()) ? userObject[user.Key.ToString()]["username"].ToString() : $"Deleted User - {user.Key}";
                string path = System.IO.Path.Combine(folder, purchasedTabCollection.Username);
                if (Path.Exists(path))
                {
                    foreach (Purchased.List purchase in user.Value)
                    {
                        switch (purchase.responseType)
                        {
                            case "post":
                                List<long> previewids = new();
                                if (purchase.previews != null)
                                {
                                    for (int i = 0; i < purchase.previews.Count; i++)
                                    {
                                        if (!previewids.Contains((long)purchase.previews[i]))
                                        {
                                            previewids.Add((long)purchase.previews[i]);
                                        }
                                    }
                                }
                                else if (purchase.preview != null)
                                {
                                    for (int i = 0; i < purchase.preview.Count; i++)
                                    {
                                        if (!previewids.Contains((long)purchase.preview[i]))
                                        {
                                            previewids.Add((long)purchase.preview[i]);
                                        }
                                    }
                                }
                                await m_DBHelper.AddPost(path, purchase.id, purchase.text != null ? purchase.text : string.Empty, purchase.price != null ? purchase.price.ToString() : "0", purchase.price != null && purchase.isOpened ? true : false, purchase.isArchived.HasValue ? purchase.isArchived.Value : false, purchase.createdAt != null ? purchase.createdAt.Value : purchase.postedAt.Value);
                                purchasedTabCollection.PaidPosts.PaidPostObjects.Add(purchase);
                                foreach (Messages.Medium medium in purchase.media)
                                {
                                    if (medium.type == "photo" && !config.DownloadImages)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "video" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "gif" && !config.DownloadVideos)
                                    {
                                        continue;
                                    }
                                    if (medium.type == "audio" && !config.DownloadAudios)
                                    {
                                        continue;
                                    }
                                    if (previewids.Count > 0)
                                    {
                                        bool has = previewids.Any(cus => cus.Equals(medium.id));
                                        if (!has && medium.canView && medium.source != null && medium.source.source != null && !medium.source.source.Contains("upload"))
                                        {

                                            if (!purchasedTabCollection.PaidPosts.PaidPosts.ContainsKey(medium.id))
                                            {
                                                await m_DBHelper.AddMedia(path, medium.id, purchase.id, medium.source.source, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                                purchasedTabCollection.PaidPosts.PaidPosts.Add(medium.id, medium.source.source);
                                                purchasedTabCollection.PaidPosts.PaidPostMedia.Add(medium);
                                            }
                                        }
                                        else if (!has && medium.canView && medium.files != null && medium.files.drm != null)
                                        {

                                            if (!purchasedTabCollection.PaidPosts.PaidPosts.ContainsKey(medium.id))
                                            {
                                                await m_DBHelper.AddMedia(path, medium.id, purchase.id, medium.files.drm.manifest.dash, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                                purchasedTabCollection.PaidPosts.PaidPosts.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{purchase.id}");
                                                purchasedTabCollection.PaidPosts.PaidPostMedia.Add(medium);
                                            }

                                        }
                                    }
                                    else
                                    {
                                        if (medium.canView && medium.source != null && medium.source.source != null && !medium.source.source.Contains("upload"))
                                        {
                                            if (!purchasedTabCollection.PaidPosts.PaidPosts.ContainsKey(medium.id))
                                            {
                                                await m_DBHelper.AddMedia(path, medium.id, purchase.id, medium.source.source, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                                purchasedTabCollection.PaidPosts.PaidPosts.Add(medium.id, medium.source.source);
                                                purchasedTabCollection.PaidPosts.PaidPostMedia.Add(medium);
                                            }
                                        }
                                        else if (medium.canView && medium.files != null && medium.files.drm != null)
                                        {
                                            if (!purchasedTabCollection.PaidPosts.PaidPosts.ContainsKey(medium.id))
                                            {
                                                await m_DBHelper.AddMedia(path, medium.id, purchase.id, medium.files.drm.manifest.dash, null, null, null, "Posts", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), previewids.Contains(medium.id) ? true : false, false, null);
                                                purchasedTabCollection.PaidPosts.PaidPosts.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{purchase.id}");
                                                purchasedTabCollection.PaidPosts.PaidPostMedia.Add(medium);
                                            }
                                        }
                                    }
                                }
                                break;
                            case "message":
                                if (purchase.postedAt != null)
                                {
                                    await m_DBHelper.AddMessage(path, purchase.id, purchase.text != null ? purchase.text : string.Empty, purchase.price != null ? purchase.price : "0", true, false, purchase.postedAt.Value, purchase.fromUser.id);
                                }
                                else
                                {
                                    await m_DBHelper.AddMessage(path, purchase.id, purchase.text != null ? purchase.text : string.Empty, purchase.price != null ? purchase.price : "0", true, false, purchase.createdAt.Value, purchase.fromUser.id);
                                }
                                purchasedTabCollection.PaidMessages.PaidMessageObjects.Add(purchase);
                                if (purchase.media != null && purchase.media.Count > 0)
                                {
                                    List<long> paidMessagePreviewids = new();
                                    if (purchase.previews != null)
                                    {
                                        for (int i = 0; i < purchase.previews.Count; i++)
                                        {
                                            if (!paidMessagePreviewids.Contains((long)purchase.previews[i]))
                                            {
                                                paidMessagePreviewids.Add((long)purchase.previews[i]);
                                            }
                                        }
                                    }
                                    else if (purchase.preview != null)
                                    {
                                        for (int i = 0; i < purchase.preview.Count; i++)
                                        {
                                            if (!paidMessagePreviewids.Contains((long)purchase.preview[i]))
                                            {
                                                paidMessagePreviewids.Add((long)purchase.preview[i]);
                                            }
                                        }
                                    }

                                    foreach (Messages.Medium medium in purchase.media)
                                    {
                                        if (paidMessagePreviewids.Count > 0)
                                        {
                                            bool has = paidMessagePreviewids.Any(cus => cus.Equals(medium.id));
                                            if (!has && medium.canView && medium.source != null && medium.source.source != null && !medium.source.source.Contains("upload"))
                                            {
                                                if (medium.type == "photo" && !config.DownloadImages)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "video" && !config.DownloadVideos)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "gif" && !config.DownloadVideos)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "audio" && !config.DownloadAudios)
                                                {
                                                    continue;
                                                }
                                                if (!purchasedTabCollection.PaidMessages.PaidMessages.ContainsKey(medium.id))
                                                {
                                                    await m_DBHelper.AddMedia(path, medium.id, purchase.id, medium.source.source, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), paidMessagePreviewids.Contains(medium.id) ? true : false, false, null);
                                                    purchasedTabCollection.PaidMessages.PaidMessages.Add(medium.id, medium.source.source);
                                                    purchasedTabCollection.PaidMessages.PaidMessageMedia.Add(medium);
                                                }
                                            }
                                            else if (!has && medium.canView && medium.files != null && medium.files.drm != null)
                                            {
                                                if (medium.type == "photo" && !config.DownloadImages)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "video" && !config.DownloadVideos)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "gif" && !config.DownloadVideos)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "audio" && !config.DownloadAudios)
                                                {
                                                    continue;
                                                }
                                                if (!purchasedTabCollection.PaidMessages.PaidMessages.ContainsKey(medium.id))
                                                {
                                                    await m_DBHelper.AddMedia(path, medium.id, purchase.id, medium.files.drm.manifest.dash, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), paidMessagePreviewids.Contains(medium.id) ? true : false, false, null);
                                                    purchasedTabCollection.PaidMessages.PaidMessages.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{purchase.id}");
                                                    purchasedTabCollection.PaidMessages.PaidMessageMedia.Add(medium);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (medium.canView && medium.source != null && medium.source.source != null && !medium.source.source.Contains("upload"))
                                            {
                                                if (medium.type == "photo" && !config.DownloadImages)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "video" && !config.DownloadVideos)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "gif" && !config.DownloadVideos)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "audio" && !config.DownloadAudios)
                                                {
                                                    continue;
                                                }
                                                if (!purchasedTabCollection.PaidMessages.PaidMessages.ContainsKey(medium.id))
                                                {
                                                    await m_DBHelper.AddMedia(path, medium.id, purchase.id, medium.source.source, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), paidMessagePreviewids.Contains(medium.id) ? true : false, false, null);
                                                    purchasedTabCollection.PaidMessages.PaidMessages.Add(medium.id, medium.source.source);
                                                    purchasedTabCollection.PaidMessages.PaidMessageMedia.Add(medium);
                                                }
                                            }
                                            else if (medium.canView && medium.files != null && medium.files.drm != null)
                                            {
                                                if (medium.type == "photo" && !config.DownloadImages)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "video" && !config.DownloadVideos)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "gif" && !config.DownloadVideos)
                                                {
                                                    continue;
                                                }
                                                if (medium.type == "audio" && !config.DownloadAudios)
                                                {
                                                    continue;
                                                }
                                                if (!purchasedTabCollection.PaidMessages.PaidMessages.ContainsKey(medium.id))
                                                {
                                                    await m_DBHelper.AddMedia(path, medium.id, purchase.id, medium.files.drm.manifest.dash, null, null, null, "Messages", medium.type == "photo" ? "Images" : (medium.type == "video" || medium.type == "gif" ? "Videos" : (medium.type == "audio" ? "Audios" : null)), paidMessagePreviewids.Contains(medium.id) ? true : false, false, null);
                                                    purchasedTabCollection.PaidMessages.PaidMessages.Add(medium.id, $"{medium.files.drm.manifest.dash},{medium.files.drm.signature.dash.CloudFrontPolicy},{medium.files.drm.signature.dash.CloudFrontSignature},{medium.files.drm.signature.dash.CloudFrontKeyPairId},{medium.id},{purchase.id}");
                                                    purchasedTabCollection.PaidMessages.PaidMessageMedia.Add(medium);
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                        }
                    }
                    purchasedTabCollections.Add(purchasedTabCollection);
                }
            }
            return purchasedTabCollections;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<string> GetDRMMPDPSSH(string mpdUrl, string policy, string signature, string kvp)
    {
        try
        {
            string pssh = null;

            HttpClient client = new();
            HttpRequestMessage request = new(HttpMethod.Get, mpdUrl);
            request.Headers.Add("user-agent", auth.USER_AGENT);
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Cookie", $"CloudFront-Policy={policy}; CloudFront-Signature={signature}; CloudFront-Key-Pair-Id={kvp}; {auth.COOKIE};");
            using (var response = await client.SendAsync(request))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
                XNamespace cenc = "urn:mpeg:cenc:2013";
                XDocument xmlDoc = XDocument.Parse(body);
                var psshElements = xmlDoc.Descendants(cenc + "pssh");
                pssh = psshElements.ElementAt(1).Value;
            }

            return pssh;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<DateTime> GetDRMMPDLastModified(string mpdUrl, string policy, string signature, string kvp)
    {
        Log.Debug("Calling GetDRMMPDLastModified");
        Log.Debug($"mpdUrl: {mpdUrl}");
        Log.Debug($"policy: {policy}");
        Log.Debug($"signature: {signature}");
        Log.Debug($"kvp: {kvp}");

        try
        {
            DateTime lastmodified;

            HttpClient client = new();
            HttpRequestMessage request = new(HttpMethod.Get, mpdUrl);
            request.Headers.Add("user-agent", auth.USER_AGENT);
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Cookie", $"CloudFront-Policy={policy}; CloudFront-Signature={signature}; CloudFront-Key-Pair-Id={kvp}; {auth.COOKIE};");
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                lastmodified = response.Content.Headers.LastModified?.LocalDateTime ?? DateTime.Now;

                Log.Debug($"Last modified: {lastmodified}");
            }
            return lastmodified;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return DateTime.Now;
    }


    public async Task<string> GetDecryptionKey(Dictionary<string, string> drmHeaders, string licenceURL, string pssh)
    {
        Log.Debug("Calling GetDecryptionKey");

        try
        {
            string dcValue = string.Empty;

            StringBuilder sb = new();
            sb.Append("{\n");
            sb.AppendFormat("  \"License URL\": \"{0}\",\n", licenceURL);
            sb.Append("  \"Headers\": \"{");
            foreach (KeyValuePair<string, string> header in drmHeaders)
            {
                if (header.Key == "time" || header.Key == "user-id")
                {
                    sb.AppendFormat("\\\"{0}\\\": \\\"{1}\\\",", header.Key, header.Value);
                }
                else
                {
                    sb.AppendFormat("\\\"{0}\\\": \\\"{1}\\\",", header.Key, header.Value);
                }
            }
            sb.Remove(sb.Length - 1, 1);
            sb.Append("}\",\n");
            sb.AppendFormat("  \"PSSH\": \"{0}\"\n", pssh);
            sb.Append(",\"JSON\":\"\",\"Cookies\":\"\",\"Data\":\"\",\"Proxy\":\"\"");
            sb.Append('}');
            string json = sb.ToString();
            HttpClient client = new();

            Log.Debug($"Posting to CDRM Project: {json}");

            HttpRequestMessage request = new(HttpMethod.Post, "https://cdrm-project.com/")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request);

            Log.Debug($"CDRM Project Response: {response}");

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();

            var doc = JsonDocument.Parse(body);

            dcValue = doc.RootElement.GetProperty("Message").GetString().Trim();

            return dcValue;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }


    public async Task<string> GetDecryptionKeyNew(Dictionary<string, string> drmHeaders, string licenceURL, string pssh)
    {
        Log.Debug("Calling GetDecryptionKeyNew");

        try
        {
            var resp1 = PostData(licenceURL, drmHeaders, new byte[] { 0x08, 0x04 });
            var certDataB64 = Convert.ToBase64String(resp1);
            var cdm = new CDMApi();
            var challenge = cdm.GetChallenge(pssh, certDataB64, false, false);
            var resp2 = PostData(licenceURL, drmHeaders, challenge);
            var licenseB64 = Convert.ToBase64String(resp2);
            cdm.ProvideLicense(licenseB64);
            List<ContentKey> keys = cdm.GetKeys();
            if (keys.Count > 0)
            {
                return keys[0].ToString();
            }

            Log.Debug($"resp1: {resp1}");
            Log.Debug($"certDataB64: {certDataB64}");
            Log.Debug($"challenge: {challenge}");
            Log.Debug($"resp2: {resp2}");
            Log.Debug($"licenseB64: {licenseB64}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine("\nInner Exception:");
                Console.WriteLine("Exception caught: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
        return null;
    }
}
