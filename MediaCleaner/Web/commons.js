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
        {
            href: getConfigurationPageUrl('MediaCleaner_Locations'),
            name: 'Locations'
        },
        {
            href: getConfigurationPageUrl('MediaCleaner_Troubleshooting'),
            name: 'Troubleshooting'
        },
    ]
    return tabs
}

export const TabGeneral = 0
export const TabUsers = 1
export const TabLocations = 2
export const TabTroubleshooting = 3

const getConfigurationPageUrl = (name) => 'configurationpage?name=' + encodeURIComponent(name)

export function fireEvent(elements, event) {
    elements = Array.isArray(elements) ? elements : [elements]
    elements.forEach(x => x.dispatchEvent(new Event(event)))
}
