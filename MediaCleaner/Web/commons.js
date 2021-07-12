export const pluginId = '607fee77-97eb-41fe-bf22-26844d99ffb0'

export function getTabs() {
    const tabs = [
        {
            href: getConfigurationPageUrl('MediaCleaner'),
            name: 'General'
        },
        {
            href: getConfigurationPageUrl('MediaCleaner_Users'),
            name: 'Users'
        },
    ]
    return tabs
}

const getConfigurationPageUrl = (name) => ApiClient.getUrl('web/ConfigurationPage', { name })
