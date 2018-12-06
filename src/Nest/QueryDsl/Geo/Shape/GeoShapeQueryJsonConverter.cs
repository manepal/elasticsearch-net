﻿using System;
using Utf8Json;
using Utf8Json.Internal;
using Utf8Json.Resolvers;

namespace Nest
{
	internal class GeoShapeQueryFieldNameFormatter : IJsonFormatter<IGeoShapeQuery>
	{
		public IGeoShapeQuery Deserialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver) => throw new NotSupportedException();

		public void Serialize(ref JsonWriter writer, IGeoShapeQuery value, IJsonFormatterResolver formatterResolver)
		{
			var fieldName = value.Field;
			if (fieldName == null)
			{
				writer.WriteNull();
				return;
			}

			var settings = formatterResolver.GetConnectionSettings();
			var field = settings.Inferrer.Field(fieldName);

			if (field.IsNullOrEmpty())
			{
				writer.WriteNull();
				return;
			}

			writer.WriteBeginObject();
			var name = value.Name;
			var boost = value.Boost;
			var ignoreUnmapped = value.IgnoreUnmapped;

			if (!name.IsNullOrEmpty())
			{
				writer.WritePropertyName("_name");
				writer.WriteString(name);
				writer.WriteValueSeparator();
			}
			if (boost != null)
			{
				writer.WritePropertyName("boost");
				writer.WriteDouble(boost.Value);
				writer.WriteValueSeparator();
			}
			if (ignoreUnmapped != null)
			{
				writer.WritePropertyName("ignore_unmapped");
				writer.WriteBoolean(ignoreUnmapped.Value);
				writer.WriteValueSeparator();
			}

			writer.WritePropertyName(field);
			var shapeFormatter = DynamicObjectResolver.ExcludeNullCamelCase.GetFormatter<IGeoShapeQuery>();
			shapeFormatter.Serialize(ref writer, value, formatterResolver);
			writer.WriteEndObject();
		}
	}

	internal class GeoShapeQueryFormatter : IJsonFormatter<IGeoShapeQuery>
	{
		private static readonly AutomataDictionary AutomataDictionary = new AutomataDictionary
		{
			{ "boost", 0 },
			{ "_name", 1 },
			{ "ignore_unmapped", 2 },
			{ "relation", 3 }
		};

		private static readonly AutomataDictionary ShapeDictionary = new AutomataDictionary
		{
			{ "shape", 0 },
			{ "indexed_shape", 1 }
		};

		public IGeoShapeQuery Deserialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver)
		{
			var token = reader.GetCurrentJsonToken();

			if (token == JsonToken.Null)
				return null;

			var count = 0;
			string field = null;
			double? boost = null;
			string name = null;
			bool? ignoreUnmapped = null;
			IGeoShapeQuery query = null;
			GeoShapeRelation? relation = null;

			while (reader.ReadIsInObject(ref count))
			{
				var propertyName = reader.ReadPropertyNameSegmentRaw();
				if (AutomataDictionary.TryGetValue(propertyName, out var value))
				{
					switch (value)
					{
						case 0:
							boost = reader.ReadDouble();
							break;
						case 1:
							name = reader.ReadString();
							break;
						case 2:
							ignoreUnmapped = reader.ReadBoolean();
							break;
						case 3:
							relation = formatterResolver.GetFormatter<GeoShapeRelation>()
								.Deserialize(ref reader, formatterResolver);
							break;
					}
				}
				else
				{
					field = propertyName.Utf8String();
					if (reader.ReadIsBeginObject())
					{
						reader.ReadNext();
						var shapeProperty = reader.ReadPropertyNameSegmentRaw();
						if (ShapeDictionary.TryGetValue(shapeProperty, out var shapeValue))
						{
							switch (shapeValue)
							{
								case 0:
									var shapeFormatter = formatterResolver.GetFormatter<IGeoShape>();
									query = new GeoShapeQuery
									{
										Shape = shapeFormatter.Deserialize(ref reader, formatterResolver)
									};
									break;
								case 1:
									var fieldLookupFormatter = formatterResolver.GetFormatter<FieldLookup>();
									query = new GeoShapeQuery
									{
										IndexedShape = fieldLookupFormatter.Deserialize(ref reader, formatterResolver)
									};
									break;
							}
						}
					}
				}
			}

			if (query == null)
				return null;

			query.Boost = boost;
			query.Name = name;
			query.Field = field;
			query.Relation = relation;
			query.IgnoreUnmapped = ignoreUnmapped;
			return query;
		}

		public void Serialize(ref JsonWriter writer, IGeoShapeQuery value, IJsonFormatterResolver formatterResolver) =>
			throw new NotSupportedException();
	}
}
