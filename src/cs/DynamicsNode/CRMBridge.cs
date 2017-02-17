using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;

namespace DynamicsNode
{
    public class CRMBridge
    {
        private IOrganizationService _service;
        private Dictionary<string, EntityMetadata> _metadataCache = new Dictionary<string, EntityMetadata>();

        public CRMBridge(string connectionString, bool useFake)
        {
            if (useFake)
            {
                _service = new FakeService(connectionString);
            }
            else
            {
                _service = new CrmService(connectionString);
            }
        }

        public object Delete(dynamic options)
        {
            string entityName = options.entityName;
            Guid id = new Guid(options.id);

            _service.Delete(entityName, id);

            return null;
        }

        public object Create(dynamic entity)
        {
            Guid createdId = Guid.Empty;
            Entity e = ConvertFromDynamic(entity);
            createdId = _service.Create(e);
            return createdId;
        }

        public void Update(dynamic entity)
        {
            // convert the values to an entity type
            Entity e = ConvertFromDynamic(entity);
            _service.Update(e);
        }

        public object Retrieve(dynamic options)
        {
            object[] result = null;

            string entityName = options.entityName;
            Guid id = new Guid(options.id);
            ColumnSet columns = new ColumnSet(true);

            // convert the columns option to the right type
            if (options.columns.GetType() == typeof(bool))
            {
                columns = new ColumnSet((bool)options.columns);
            }
            else
            {
                string[] cols = new string[options.columns.Length];
                ((object[])options.columns).CopyTo(cols, 0);

                columns = new ColumnSet(cols);
            }

            Entity entityRecord = null;
            entityRecord = _service.Retrieve(entityName, id, columns);

            if (entityRecord != null)
            {
                result = Convert(entityRecord);
            }
            return result;
        }

        private QueryExpression FetchXmlToQueryExpression(string fetchXml)
        {
            return ((FetchXmlToQueryExpressionResponse)_service.Execute(new FetchXmlToQueryExpressionRequest() { FetchXml = fetchXml })).Query;

        }

        public object RetrieveMultiple(string fetchXml)
        {
            var result = new List<object>();

            // validate parameters
            if (fetchXml == null || string.IsNullOrWhiteSpace(fetchXml)) throw new Exception("fetchXml not specified");

            var query = FetchXmlToQueryExpression(fetchXml);
            var foundRecords = _service.RetrieveMultiple(query);

            if (foundRecords != null && foundRecords.Entities != null && foundRecords.Entities.Count > 0)
            {
                for (int i = 0; i < foundRecords.Entities.Count; i++)
                {
                    var record = foundRecords.Entities[i];
                    var convertedRecord = Convert(record);
                    result.Add(convertedRecord);
                }
            }

            return result.ToArray();
        }

        public object Associate(dynamic options)
        {
            string entityName = options.entityName;
            Guid entityId = new Guid(options.entityId);
            Relationship relationship = new Relationship(options.relationship);
            var relatedEntitiesList = new List<EntityReference>();
            foreach (var rel in options.relatedEntities)
            {
                relatedEntitiesList.Add(new EntityReference(rel.entityName, new Guid(rel.entityId)));
            }
            EntityReferenceCollection relatedEntities = new EntityReferenceCollection(relatedEntitiesList);
            _service.Associate(entityName, entityId, relationship, relatedEntities);

            return null;
        }

        public object Disassociate(dynamic options)
        {
            string entityName = options.entityName;
            Guid entityId = new Guid(options.entityId);
            Relationship relationship = new Relationship(options.relationship);
            var relatedEntitiesList = new List<EntityReference>();
            foreach (var rel in options.relatedEntities)
            {
                relatedEntitiesList.Add(new EntityReference(rel.entityName, new Guid(rel.entityId)));
            }
            EntityReferenceCollection relatedEntities = new EntityReferenceCollection(relatedEntitiesList);
            _service.Disassociate(entityName, entityId, relationship, relatedEntities);

            return null;
        }

        private object[] Convert(Entity entityRecord)
        {
            var values = new List<object>();
            string[] entityAttributes = new string[entityRecord.Attributes.Keys.Count];
            entityRecord.Attributes.Keys.CopyTo(entityAttributes, 0);

            for (int i = 0; i < entityAttributes.Length; i++)
            {
                string attributeName = entityAttributes[i];
                object attributeValue = entityRecord.Attributes[entityAttributes[i]];
                if (attributeValue.GetType() == typeof(EntityReference))
                {
                    var er = (EntityReference)attributeValue;
                    values.Add(new object[] { attributeName, er.Id });
                    values.Add(new object[] { string.Format("{0}_name", attributeName), er.Name });
                    values.Add(new object[] { string.Format("{0}_type", attributeName), er.LogicalName });
                }
                else if (attributeValue.GetType() == typeof(OptionSetValue))
                {
                    var os = (OptionSetValue)attributeValue;
                    values.Add(new object[] { attributeName, os.Value });
                    // Add attribute text from metadata
                }
                else
                {
                    values.Add(new object[] { attributeName, attributeValue });
                }
            }
            return values.ToArray();
        }

        public object Execute(dynamic request)
        {
            OrganizationRequest objRequest = ConvertFromDynamic(request);
            object response = _service.Execute(objRequest);

            if (response != null && response.GetType() == typeof(WhoAmIResponse))
            {
                var rs = (WhoAmIResponse)response;
                response = new
                {
                    UserId = rs.UserId,
                    BusinessUnitId = rs.BusinessUnitId,
                    OrganizationId = rs.OrganizationId,
                    ExtensionData = rs.ExtensionData,
                    //Results = rs.Results,
                    ResponseName = rs.ResponseName
                };
            }

            if (response != null && response.GetType() == typeof(RetrieveEntityResponse))
            {
                var getTargets = new Func<LookupAttributeMetadata, string[]>(x => x != null ? x.Targets : null);
                var getLabel = new Func<Label, object>(x => x != null ?
                    new { UserLocalizedLabel = x.UserLocalizedLabel != null ? new { Label = x.UserLocalizedLabel.Label } : null } : null
                );

                var getOptionSetOptionItem = new Func<OptionMetadata, object>(x => new { Label = getLabel(x.Label), Value = x.Value });
                var getOptionOptions = new Func<OptionMetadata[], object>(x => x.Select(getOptionSetOptionItem));

                var getPicklistOptionset = new Func<PicklistAttributeMetadata, object>(x => x != null ?
                    new { Options = x.OptionSet.Options != null ? getOptionOptions(x.OptionSet.Options.ToArray()) : null } : null);

                var getBooleanOptionset = new Func<BooleanAttributeMetadata, object>(x => x != null ?
                    new
                    {
                        TrueOption = x.OptionSet.TrueOption != null ? getOptionSetOptionItem(x.OptionSet.TrueOption) : null,
                        FalseOption = x.OptionSet.FalseOption != null ? getOptionSetOptionItem(x.OptionSet.FalseOption) : null
                    } : null);

                var getOptionSet = new Func<AttributeMetadata, object>(x => x.GetType() == typeof(PicklistAttributeMetadata) ?
                                        getPicklistOptionset(x as PicklistAttributeMetadata) : x.GetType() == typeof(BooleanAttributeMetadata) ? getBooleanOptionset(x as BooleanAttributeMetadata) : null);

                var rs = (RetrieveEntityResponse)response;
                response = new
                {
                    EntityMetadata = new
                    {
                        PrimaryIdAttribute = rs.EntityMetadata.PrimaryIdAttribute,
                        SchemaName = rs.EntityMetadata.SchemaName,
                        IsActivity = rs.EntityMetadata.IsActivity,
                        Attributes =
                            rs.EntityMetadata.Attributes.Select(x => new
                            {
                                LogicalName = x.LogicalName,
                                AttributeType = x.AttributeType,
                                DisplayName = getLabel(x.DisplayName),
                                OptionSet = getOptionSet(x),
                                Targets = getTargets(x as LookupAttributeMetadata)
                            })
                    }
                };
            }

            return response;
        }

        private Assembly GetAssembly(string name)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == name);
            return assembly;
        }

        private object ConvertFromDynamic(ExpandoObject value)
        {
            object converted = null;
            var valueDictionary = (IDictionary<string, object>)value;
            string typeName = (string)valueDictionary["__typeName"];

            if (string.IsNullOrEmpty(typeName)) throw new Exception("Class Type Name not specified");

            converted = GetTypeInstance(typeName);
            Type convertedType = converted.GetType();

            foreach (var prop in valueDictionary)
            {
                if (prop.Value != null)
                {
                    var propDef = convertedType.GetProperty(prop.Key);
                    if (propDef != null)
                    {
                        var propValue = prop.Value;
                        Type propValueType = prop.Value.GetType();
                        if (propDef.PropertyType == typeof(AttributeCollection))
                        {
                            if (propValueType != typeof(ExpandoObject)) throw new Exception(string.Format("Can't convert from {0} to AttributeCollection", propValueType.Name));
                            propValue = ConvertFromDynamicToAttributeCollection((ExpandoObject)propValue);
                        }
                        else if (propValueType == typeof(ExpandoObject))
                        {
                            ExpandoObject propExpando = (ExpandoObject)propValue;
                            propValue = ConvertFromDynamic(propExpando);
                        }
                        else if (propDef.PropertyType == typeof(Guid) && propValueType == typeof(string))
                        {
                            propValue = new Guid((string)propValue);
                        }
                        propDef.SetValue(converted, propValue);
                    }
                }
            }
            return converted;
        }

        private AttributeCollection ConvertFromDynamicToAttributeCollection(ExpandoObject value)
        {
            AttributeCollection retVal = null;
            if (value != null)
            {
                var valueDictionary = (IDictionary<string, object>)value;
                if (valueDictionary.Keys.Count > 0) retVal = new AttributeCollection();
                foreach (var prop in valueDictionary)
                {
                    if (prop.Value != null)
                    {
                        // Convert values
                        object convertedValue = prop.Value;
                        var propValueType = prop.Value.GetType();

                        if (propValueType == typeof(ExpandoObject))
                        {
                            convertedValue = ConvertFromDynamic((ExpandoObject)prop.Value);
                        }
                        else if (propValueType.IsArray)
                        {
                            convertedValue = ConvertFromArray((Array)prop.Value);
                        }
                        else if (propValueType == typeof(string))
                        {
                            Guid guidValue;
                            if (Guid.TryParse((string)prop.Value, out guidValue))
                            {
                                convertedValue = guidValue;
                            }
                        }
                        retVal.Add(prop.Key, convertedValue);
                    }
                }
            }
            return retVal;
        }

        private object ConvertFromArray(Array arrayToConvert)
        {
            object retVal = null;

            if (arrayToConvert != null)
            {
                var convertedItems = new List<object>();

                Type lastItemType = null;
                bool sameTypeArray = true;
                foreach (var item in arrayToConvert)
                {
                    object convertedValue = null;
                    if (item != null)
                    {
                        if (item.GetType() != typeof(ExpandoObject)) throw new Exception("The element in the array is not an ExpandoObject.Check the PartyList attributes in your entity");
                        var expandoItem = (ExpandoObject)item;
                        convertedValue = ConvertFromDynamic(expandoItem);
                        if (convertedValue != null)
                        {
                            if (lastItemType != null && lastItemType != convertedValue.GetType()) sameTypeArray = false;
                            lastItemType = convertedValue.GetType();
                        }
                    }
                    convertedItems.Add(convertedValue);
                }
                if (sameTypeArray && lastItemType != null)
                {
                    // If all the items in the array are ExpandoObjects and their underlying type is the same, then return a specific type array
                    retVal = Array.CreateInstance(lastItemType, convertedItems.Count);
                    Array.Copy(convertedItems.ToArray(), (Array)retVal, convertedItems.Count);
                }
                else
                {
                    retVal = convertedItems.ToArray();
                }
            }

            return retVal;
        }

        private object GetTypeInstance(string typeFullName)
        {
            object typeInstance;
            var typeNameParts = typeFullName.Split(',');
            string assemblyName = typeNameParts[0], className = typeNameParts[1];

            var assembly = GetAssembly(assemblyName);
            if (assembly == null) throw new Exception(string.Format("Can't find assembly '{0}'", assemblyName));
            typeInstance = assembly.CreateInstance(className);

            if (typeInstance == null) throw new Exception(string.Format("Can't create class of type '{0}'", typeFullName));
            return typeInstance;
        }
    }
}

