using System;
using System.Text.RegularExpressions;
using Kayak;
using Newtonsoft.Json.Linq;

namespace Rhino.DivanDB.Server.Responders
{
    public class Document : KayakResponder
    {
        public override string UrlPattern
        {
            get { return @"/docs/([\w\d_-]+)"; }
        }

        public override string[] SupportedVerbs
        {
            get { return new[] { "GET", "DELETE", "PUT" }; }
        }

        protected override void Respond(KayakContext context)
        {
            var match = urlMatcher.Match(context.Request.RequestUri);
            var docId = match.Groups[1].Value;
            switch (context.Request.Verb)
            {
                case "GET":
                    context.Response.Headers["Content-Type"] = "application/json; charset=utf-8";
                    context.WriteData(Database.Get(docId));
                    break;
                case "DELETE":
                    Database.Delete(docId);
                    context.Response.SetStatusToDeleted();
                    break;
                case "PUT":
                    Put(context, docId);
                    break;
            }
        }

        private void Put(KayakContext context, string docId)
        {
            context.Response.SetStatusToCreated();
            var json = context.ReadJson();
            var idProp = json.Property("_id");
            if (idProp == null) // set the in-document id based on the url
            {
                json.Add("_id", new JValue(docId));
            }
            else
            {
                var idVal = idProp.Value.Value<object>();
                if (idVal != null && idVal.ToString() != docId) // doc id conflict
                {
                    context.Response.SetStatusToBadRequest();
                    var err = string.Format(
                        "PUT on {0} but the document contained '_id' property with: '{1}'",
                        context.Request.RequestUri, idVal);
                    context.Response.WriteLine(err);
                    return;
                }
            }
            context.WriteJson(new { id = Database.Put(json) });
        }
    }
}