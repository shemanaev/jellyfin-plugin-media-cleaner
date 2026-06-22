export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })
}

const pageSelector = '[data-role="page"]'
const logTextareaSelector = '#TroubleshootingLog'
const logViewerSelector = '#TroubleshootingLogViewer'
let issueMarkdown = ''

function onViewShow(commons) {
    const page = this
    commons.setTabs('MediaCleaner', commons.TabTroubleshooting, commons.getTabs)
    Dashboard.showLoadingMsg()

    if (window.isSecureContext) {
        const $TroubleshootingButtonCopy = page.querySelector('#TroubleshootingButtonCopy')
        $TroubleshootingButtonCopy.addEventListener('click', troubleshootingButtonCopyClick)
        $TroubleshootingButtonCopy.style.display = 'inline-flex'
    }

    const $TroubleshootingButtonRefreshReport = page.querySelector('#TroubleshootingButtonRefreshReport')
    $TroubleshootingButtonRefreshReport.addEventListener('click', troubleshootingButtonRefreshReportClick)

    getReport(page)
}

function getReport(page) {
    const request = {
        url: ApiClient.getUrl('MediaCleaner/Report'),
    }

    Dashboard.showLoadingMsg()
    ApiClient.fetch(request).then(normalizeReportResponse).then(function (report) {
        const log = page.querySelector(logTextareaSelector)
        const viewer = page.querySelector(logViewerSelector)
        issueMarkdown = report.issueMarkdown || report.IssueMarkdown || ''
        log.value = issueMarkdown

        viewer.innerHTML = report.formattedHtml || report.FormattedHtml || ''

        Dashboard.hideLoadingMsg()
    }).catch(function (error) {
        console.log(error)
        Dashboard.hideLoadingMsg()
        Dashboard.alert('Could not generate the troubleshooting report')
    })
}

function normalizeReportResponse(result) {
    if (!result) {
        return {}
    }

    if (typeof result === 'string') {
        return JSON.parse(result)
    }

    if (typeof result.json === 'function') {
        return result.json()
    }

    if (typeof result.text === 'function') {
        return result.text().then(text => JSON.parse(text))
    }

    return result
}

function troubleshootingButtonCopyClick(event) {
    navigator.clipboard.writeText(issueMarkdown)
        .then(() => {
            Dashboard.alert('GitHub issue report copied to clipboard')
        })
        .catch(error => {
            console.log('Error copying troubleshooting report', error)
            Dashboard.alert('Could not copy the troubleshooting report')
        })
}

function troubleshootingButtonRefreshReportClick(event) {
    const page = this.closest(pageSelector)
    getReport(page)
}
