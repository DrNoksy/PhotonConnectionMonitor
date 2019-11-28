using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace PhotonConnectionMonitor
{
	static class Utils
	{
		public static string[] GetMetaTagContent(string html, string metaItemName) {
			var metaTag = new Regex("<meta name=\"(.+?)\".*content=\"(.+?)\".*>");
			var matches = metaTag.Matches(html)
				.Where(m => m.Groups[1].Value == metaItemName && m.Groups[2].Value != null);
			return matches == null
				? null
				: matches.Select(match => match.Groups[2].Value.Trim()).ToArray();
		}

		public static string GetHttpHeader(IList<Parameter> httpHeaders, string headerName) {
			return httpHeaders.FirstOrDefault(header => header.Name == headerName)?.Value?.ToString();
		}

		public static string SerializeXml<T>(T value) {
			if (value == null) {
				return string.Empty;
			}
			var xmlSerializer = new XmlSerializer(typeof(T));
			var stringWriter = new StringWriter();
			using (var writer = XmlWriter.Create(stringWriter)) {
				xmlSerializer.Serialize(writer, value);
				return stringWriter.ToString();
			}
		}

		public static T DeserializeXml<T>(string content) {
			XmlSerializer serializer = new XmlSerializer(typeof(T));
			using (TextReader reader = new StringReader(content)) {
				return (T)serializer.Deserialize(reader);
			}
		}

		public static string GetHexString(Byte[] byteArray) => BitConverter.ToString(byteArray).Replace("-", "").ToLower();
	}
}
