export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })
}

const pageSelector = '[data-role="page"]'
const logTextareaSelector = '#TroubleshootingLog'
const logViewerSelector = '#TroubleshootingLogViewer'
const logDateFormatSelector = '#TroubleshootingLogDateFormat'
const defaultLogDateFormat = 'YYYYMMDD'
const allowedLogDateFormats = ['YYYYMMDD', 'DDMMYYYY', 'MMDDYYYY']
const logDateFormatAliases = {
    '0': 'YYYYMMDD',
    '1': 'DDMMYYYY',
    '2': 'MMDDYYYY',
    'DD/MM/YYYY': 'DDMMYYYY',
    'MM/DD/YYYY': 'MMDDYYYY',
}

function onViewShow(commons) {
    const page = this
    commons.setTabs('MediaCleaner', commons.TabTroubleshooting, commons.getTabs)
    Dashboard.showLoadingMsg()

    if (window.isSecureContext) {
        const $TroubleshootingButtonCopy = page.querySelector('#TroubleshootingButtonCopy')
        $TroubleshootingButtonCopy.addEventListener('click', troubleshootingButtonCopyClick)
        $TroubleshootingButtonCopy.style.display = 'inline-flex'
    }

    const $TroubleshootingButtonGetLog = page.querySelector('#TroubleshootingButtonGetLog')
    $TroubleshootingButtonGetLog.addEventListener('click', troubleshootingButtonGetLogClick)

    const $TroubleshootingButtonToggleViewer = page.querySelector('#TroubleshootingButtonToggleViewer')
    $TroubleshootingButtonToggleViewer.addEventListener('click', troubleshootingButtonToggleViewerClick)

    const $LogLevel = page.querySelector('#LogLevel')
    $LogLevel.addEventListener('change', troubleshootingButtonGetLogClick)

    const $LogDateFormat = page.querySelector(logDateFormatSelector)
    $LogDateFormat.addEventListener('change', event => troubleshootingLogDateFormatChanged(event, commons))

    loadTroubleshootingConfiguration(commons, page).then(() => getLog(page))
}

function loadTroubleshootingConfiguration(commons, page) {
    const $LogDateFormat = page.querySelector(logDateFormatSelector)

    return ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        $LogDateFormat.value = normalizeLogDateFormat(config.TroubleshootingLogDateFormat)
    }).catch(function (error) {
        console.log(error)
        $LogDateFormat.value = defaultLogDateFormat
    })
}

function getLog(page) {
    const $LogLevel = page.querySelector('#LogLevel')

    const request = {
        url: ApiClient.getUrl('MediaCleaner/Log') + getLogRequestQuery($LogLevel.value, getSelectedLogDateFormat(page)),
    }

    Dashboard.showLoadingMsg()
    ApiClient.fetch(request).then(function (result) {
        const log = page.querySelector(logTextareaSelector)
        log.value = result

        renderTroubleshootingLogViewer(page)

        Dashboard.hideLoadingMsg()
    }).catch(function (error) {
        console.log(error)
        Dashboard.hideLoadingMsg()
        Dashboard.alert('Error loading log')
    })
}

function troubleshootingButtonCopyClick(event) {
    const page = this.closest(pageSelector)
    const log = page.querySelector(logTextareaSelector)

    navigator.clipboard.writeText(log.value)
        .then(() => {
            Dashboard.alert('Log copied to clipboard')
        })
        .catch(error => {
            console.log('Error copying log', error)
        })
}

function troubleshootingButtonGetLogClick(event) {
    const page = this.closest(pageSelector)
    getLog(page)
}

function troubleshootingLogDateFormatChanged(event, commons) {
    const page = event.currentTarget.closest(pageSelector)
    const selectedFormat = normalizeLogDateFormat(event.currentTarget.value)
    event.currentTarget.value = selectedFormat

    Dashboard.showLoadingMsg()
    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        config.TroubleshootingLogDateFormat = selectedFormat
        return ApiClient.updatePluginConfiguration(commons.pluginId, config)
    }).then(result => {
        Dashboard.processPluginConfigurationUpdateResult(result)
        getLog(page)
    }).catch(function (error) {
        console.log(error)
        Dashboard.hideLoadingMsg()
        Dashboard.alert('Error saving log date format')
    })
}

function troubleshootingButtonToggleViewerClick(event) {
    const page = this.closest(pageSelector)
    const log = page.querySelector(logTextareaSelector)
    const viewer = page.querySelector(logViewerSelector)

    if (log.style.display === 'none') {
        log.style.display = 'inline'
        viewer.style.display = 'none'
    } else {
        renderTroubleshootingLogViewer(page)
        log.style.display = 'none'
        viewer.style.display = 'inline'
    }
}

function renderTroubleshootingLogViewer(page) {
    const log = page.querySelector(logTextareaSelector)
    const viewer = page.querySelector(logViewerSelector)

    const el = document.createElement('html')
    el.innerHTML = log.value
    const details = el.querySelector('details:last-of-type > pre')

    viewer.innerHTML = ''
    if (!details) {
        return
    }

    const colorizedDetails = document.createElement('div')
    const formattedDetails = formatDisplayedLogDates(details.innerHTML, getSelectedLogDateFormat(page))
    colorizedDetails.innerHTML = colorizeLog(formattedDetails, {
        'Tra': 'color: #8e8e94',
        'Deb': 'color: #007dff',
        'Inf': 'color: #00ca48',
        'War': 'color: #ff7600',
        'Err': 'color: #ff0c1b',
        'Cri': 'background-color: #ff0c1b',
    })

    viewer.appendChild(colorizedDetails)
}

function getSelectedLogDateFormat(page) {
    const $LogDateFormat = page.querySelector(logDateFormatSelector)
    const selectedFormat = normalizeLogDateFormat($LogDateFormat.value)
    $LogDateFormat.value = selectedFormat
    return selectedFormat
}

export function getLogRequestQuery(level, dateFormat) {
    const query = new URLSearchParams()
    query.set('level', level || 'Trace')
    query.set('dateFormat', normalizeLogDateFormat(dateFormat))
    return `?${query.toString()}`
}

export function normalizeLogDateFormat(value) {
    const stringValue = value === undefined || value === null ? '' : String(value)
    const mappedValue = logDateFormatAliases[stringValue] || stringValue

    if (allowedLogDateFormats.includes(mappedValue)) {
        return mappedValue
    }

    return defaultLogDateFormat
}

export function formatDisplayedLogDates(s, dateFormat) {
    const selectedFormat = normalizeLogDateFormat(dateFormat)

    return s.replace(
        /(^|[^\d])(\d{4})(\d{2})(\d{2})(\s+)(\d{1,2}:\d{2}:\d{2}(?:\s?[AP]M)?)(?!\d)/g,
        (match, prefix, year, month, day, separator, time) => {
            const monthNumber = Number(month)
            const dayNumber = Number(day)

            if (monthNumber < 1 || monthNumber > 12 || dayNumber < 1 || dayNumber > 31) {
                return match
            }

            return `${prefix}${formatLogDate(year, month, day, selectedFormat)}${separator}${time}`
        })
}

function formatLogDate(year, month, day, dateFormat) {
    switch (dateFormat) {
        case 'DDMMYYYY':
            return `${day}/${month}/${year}`
        case 'MMDDYYYY':
            return `${month}/${day}/${year}`
        default:
            return `${year}${month}${day}`
    }
}

function colorizeLog(s, colors) {
    let level = Object.keys(colors)
    let style = Object.values(colors)

    let replacements = {}
    level.forEach((tag, i) => replacements['\\[(' + tag + ')\\]'] = '[<span style="' + style[i] + '">$1</span>]')
    let result = replaceBulk(s, replacements)
    return result.replace(new RegExp('\n', 'g'), '<br>\n')
}

function replaceBulk(s, replacements) {
    let find = Object.keys(replacements)
    let replace = Object.values(replacements)
    let modifiedString = s
    find.forEach((tag, i) => modifiedString = modifiedString.replace(new RegExp(tag, 'g'), replace[i]))
    return modifiedString
}
