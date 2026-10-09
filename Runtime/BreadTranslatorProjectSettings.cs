using UnityEngine;

namespace BreadTranslator.Unity
{
    public sealed class BreadTranslatorProjectSettings : ScriptableObject
    {
        public const string DefaultAssetPath = "Assets/Editor/BreadTranslatorProjectSettings.asset";

        [SerializeField]
        private string apiBaseUrl = "https://breadlingo.com";

        [SerializeField]
        private string workspaceSlug = "";

        [SerializeField]
        private string projectSlug = "";

        [SerializeField]
        private string branch = "main";

        [SerializeField]
        private string defaultSchema = "default";

        [SerializeField]
        private string localeMappings = "";

        [SerializeField]
        private string sourceLocaleCode = "en";

        [SerializeField]
        private string targetLocaleCode = "ja";

        [SerializeField]
        private string exportDirectory = "Localization/BreadLingo";

        [SerializeField]
        [Tooltip("Comma or newline separated schema field keys permitted in screenshot and LQA metadata.")]
        private string contextMetadataAllowlist = "scene,hierarchyPath,prefabPath,locale,buildVersion,buildTarget,unityVersion,resolution,branch,tableCollectionGuid,entryId,stringKey";

        public string ApiBaseUrl
        {
            get => apiBaseUrl;
            set => apiBaseUrl = value;
        }

        public string WorkspaceSlug
        {
            get => workspaceSlug;
            set => workspaceSlug = value;
        }

        public string ProjectSlug
        {
            get => projectSlug;
            set => projectSlug = value;
        }

        public string Branch
        {
            get => branch;
            set => branch = value;
        }

        public string DefaultSchema
        {
            get => defaultSchema;
            set => defaultSchema = value;
        }

        public string LocaleMappings
        {
            get => localeMappings;
            set => localeMappings = value;
        }

        public string SourceLocaleCode
        {
            get => sourceLocaleCode;
            set => sourceLocaleCode = value;
        }

        public string TargetLocaleCode
        {
            get => targetLocaleCode;
            set => targetLocaleCode = value;
        }

        public string ExportDirectory
        {
            get => exportDirectory;
            set => exportDirectory = value;
        }

        public string ContextMetadataAllowlist
        {
            get => contextMetadataAllowlist;
            set => contextMetadataAllowlist = value;
        }
    }
}
