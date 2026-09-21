using Newtonsoft.Json.Linq;

namespace RingCentral_amoCRM.Helpers;

public static class UpdateApp
{
    public static void UpdateAppSetting(string key, string value)
    {
        string filePath = "appsettings.json";
        string json = File.ReadAllText(filePath);
        JObject jsonObject = JObject.Parse(json);

        if (key.Contains(":"))
        {
            string[] keyParts = key.Split(':');
            JToken token = jsonObject;
            for (int i = 0; i < keyParts.Length - 1; i++)
            {
                token = token[keyParts[i]];
            }
            token[keyParts[^1]] = value;
        }
        else
        {
            // Update the value for a top-level key
            jsonObject[key] = value;
        }

        string updatedJson = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText(filePath, updatedJson);
    }
    
}