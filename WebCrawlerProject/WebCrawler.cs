using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace WebCrawlerProject
{
    public enum UriType { WebPage, Image, }

    public class WebCrawler
    {
        public IQueryable<KeyValuePair<UriType, Uri>> Parse(string uri)
        {
            return Parse(new Uri(uri));
        }

        public IQueryable<KeyValuePair<UriType, Uri>> Parse(Uri uri)
        {
            var u = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
            var host = new Uri(u, UriKind.Absolute);
            var aux = new List<Tuple<UriType, Uri>>();

            using (var cliente = new WebClient())
            {
                try
                {
                    var source = cliente.DownloadString(uri);
                    var doc = new HtmlAgilityPack.HtmlDocument();
                    doc.OptionMaxNestedChildNodes = 50;
                    try
                    {
                        doc.LoadHtml(source);
                    }
                    catch (Exception)
                    {
                    }
                    
                    var ret = doc.DocumentNode.SelectNodes("//img|//a")
                                              .Select(node => new
                                              {
                                                  Type = (node.Name == "img" ? UriType.Image : UriType.WebPage),
                                                  Uri = node.Attributes[(node.Name == "img" ? "src" : "href")],
                                              });

                    foreach (var r in ret)
                    {
                        try
                        {
                            if (Uri.IsWellFormedUriString(r.Uri.Value, UriKind.Absolute))
                                aux.Add(Tuple.Create(r.Type, new Uri(r.Uri.Value)));
                            else if (Uri.IsWellFormedUriString(r.Uri.Value, UriKind.Relative))
                                aux.Add(Tuple.Create(r.Type, new Uri(host, r.Uri.Value)));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }
                    }
                }
                catch
                {

                }
            }
            return aux.Select(a => new KeyValuePair<UriType, Uri>(a.Item1, a.Item2)).AsQueryable();
        }

        public byte[] LoadImage(Uri uri)
        {
            using (var cliente = new WebClient())
            {
                try
                {
                    return cliente.DownloadData(uri);
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}