export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })
}

const pageSelector = '[data-role="page"]'
const logTextareaSelector = '#TroubleshootingLog'
const logViewerSelector = '#TroubleshootingLogViewer'
const itemControlsHostSelector = '#TroubleshootingItemControlsHost'
const itemControlsSelector = '#TroubleshootingItemControls'
const itemPagerSelector = '#TroubleshootingItemPager'
const fullReportThreshold = 100
let issueMarkdown = ''
let issueMarkdownLoaded = false
let issueMarkdownLoading = null
let reportId = ''
let itemGroupCount = 0
let totalItemGroupCount = 0
let itemPageSize = 100
let itemPageStart = 0
let itemSearch = ''
let itemSearchTimer = null

function onViewShow(commons) {
    const page = this
    commons.setTabs('MediaCleaner', commons.TabTroubleshooting, commons.getTabs)
    Dashboard.showLoadingMsg()

    if (!page.dataset.mediaCleanerTroubleshootingInitialized) {
        page.dataset.mediaCleanerTroubleshootingInitialized = 'true'

        if (window.isSecureContext) {
            const $TroubleshootingButtonCopy = page.querySelector('#TroubleshootingButtonCopy')
            $TroubleshootingButtonCopy.addEventListener('click', troubleshootingButtonCopyClick)
            $TroubleshootingButtonCopy.style.display = 'inline-flex'
        }

        page.querySelector('#TroubleshootingButtonRefreshReport').addEventListener('click', troubleshootingButtonRefreshReportClick)
        page.querySelector('#TroubleshootingButtonPrevItems').addEventListener('click', troubleshootingButtonPrevItemsClick)
        page.querySelector('#TroubleshootingButtonNextItems').addEventListener('click', troubleshootingButtonNextItemsClick)
        page.querySelector('#TroubleshootingButtonDownloadFullReport').addEventListener('click', troubleshootingButtonDownloadFullReportClick)
        page.querySelector('#TroubleshootingItemSearch').addEventListener('input', troubleshootingItemSearchInput)
        page.querySelector('#TroubleshootingButtonClearItemSearch').addEventListener('click', troubleshootingButtonClearItemSearchClick)
        page.querySelector('#TroubleshootingReportSource').addEventListener('toggle', troubleshootingReportSourceToggle)
    }

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
        detachItemControls(page)
        issueMarkdown = report.issueMarkdown || report.IssueMarkdown || ''
        issueMarkdownLoaded = Boolean(issueMarkdown)
        issueMarkdownLoading = null
        reportId = report.reportId || report.ReportId || ''
        itemGroupCount = Number(report.itemGroupCount || report.ItemGroupCount || 0)
        totalItemGroupCount = Number(report.totalItemGroupCount || report.TotalItemGroupCount || itemGroupCount)
        itemPageSize = Number(report.itemPageSize || report.ItemPageSize || 100)
        itemPageStart = 0
        itemSearch = ''
        clearItemSearchTimer()

        log.value = issueMarkdown
        page.querySelector('#TroubleshootingItemSearch').value = ''
        viewer.innerHTML = report.formattedHtml || report.FormattedHtml || ''
        attachItemControls(page)
        updateItemControls(page)
        if (page.querySelector('#TroubleshootingReportSource').open) {
            loadIssueMarkdown(page)
        }

        Dashboard.hideLoadingMsg()
    }).catch(function (error) {
        console.log(error)
        Dashboard.hideLoadingMsg()
        Dashboard.alert('Could not generate the troubleshooting report')
    })
}

function getItemPage(page, start) {
    if (!reportId) {
        return
    }

    const focusState = captureItemSearchFocus(page)
    const request = {
        url: ApiClient.getUrl('MediaCleaner/ReportItems', {
            reportId: reportId,
            start: start,
            limit: itemPageSize,
            search: itemSearch,
        }),
    }

    Dashboard.showLoadingMsg()
    ApiClient.fetch(request).then(normalizeReportResponse).then(function (result) {
        detachItemControls(page)
        const itemSection = page.querySelector('#MediaCleanerItemDecisionSection')
        if (itemSection) {
            itemSection.outerHTML = getItemSectionHtml(result)
        }

        itemPageStart = Number(result.start || result.Start || start)
        itemPageSize = Number(result.limit || result.Limit || itemPageSize)
        itemGroupCount = Number(result.itemGroupCount || result.ItemGroupCount || itemGroupCount)
        totalItemGroupCount = Number(result.totalItemGroupCount || result.TotalItemGroupCount || totalItemGroupCount)
        attachItemControls(page)
        updateItemControls(page)
        restoreItemSearchFocus(page, focusState)
        Dashboard.hideLoadingMsg()
    }).catch(function (error) {
        console.log(error)
        Dashboard.hideLoadingMsg()
        Dashboard.alert('Could not load troubleshooting report page')
    })
}

function getItemSectionHtml(result) {
    const html = result.formattedHtml || result.FormattedHtml || ''
    if (html) {
        return html
    }

    return '<section class="mediaCleanerDecisionSection" id="MediaCleanerItemDecisionSection">'
        + '<h3>Item-level decisions</h3>'
        + '<p class="mediaCleanerDecisionEmpty">No matching item-level decisions.</p>'
        + '</section>'
}

function attachItemControls(page) {
    const controls = page.querySelector(itemControlsSelector)
    const itemSection = page.querySelector('#MediaCleanerItemDecisionSection')
    if (!controls || !itemSection) {
        return
    }

    const heading = itemSection.querySelector('h3')
    if (heading && heading.nextSibling) {
        itemSection.insertBefore(controls, heading.nextSibling)
    } else {
        itemSection.appendChild(controls)
    }
}

function detachItemControls(page) {
    const host = page.querySelector(itemControlsHostSelector)
    const controls = page.querySelector(itemControlsSelector)
    if (host && controls && controls.parentElement !== host) {
        host.appendChild(controls)
    }
}

function captureItemSearchFocus(page) {
    const input = page.querySelector('#TroubleshootingItemSearch')
    if (!input || document.activeElement !== input) {
        return null
    }

    return {
        start: input.selectionStart,
        end: input.selectionEnd,
    }
}

function restoreItemSearchFocus(page, focusState) {
    if (!focusState) {
        return
    }

    const input = page.querySelector('#TroubleshootingItemSearch')
    if (!input) {
        return
    }

    input.focus()
    if (typeof input.setSelectionRange === 'function'
        && focusState.start !== null
        && focusState.end !== null) {
        input.setSelectionRange(focusState.start, focusState.end)
    }
}

function updateItemControls(page) {
    const controls = page.querySelector(itemControlsSelector)
    const pager = page.querySelector(itemPagerSelector)
    const downloadButton = page.querySelector('#TroubleshootingButtonDownloadFullReport')
    if (!controls || !pager) {
        return
    }

    const hasMultiplePages = Boolean(reportId) && itemGroupCount > itemPageSize
    controls.style.display = Boolean(reportId) && totalItemGroupCount > 0 ? 'block' : 'none'
    pager.style.display = hasMultiplePages ? 'flex' : 'none'

    const from = itemGroupCount === 0 ? 0 : itemPageStart + 1
    const to = Math.min(itemGroupCount, itemPageStart + itemPageSize)
    page.querySelector('#TroubleshootingItemPagerLabel').textContent = `${from}-${to} of ${itemGroupCount}${itemSearch ? ' matching' : ''}`
    page.querySelector('#TroubleshootingButtonPrevItems').disabled = itemPageStart <= 0
    page.querySelector('#TroubleshootingButtonNextItems').disabled = itemPageStart + itemPageSize >= itemGroupCount
    page.querySelector('#TroubleshootingButtonClearItemSearch').disabled = !itemSearch
    downloadButton.style.display = totalItemGroupCount > fullReportThreshold ? 'inline-flex' : 'none'
    downloadButton.disabled = !reportId || totalItemGroupCount <= fullReportThreshold
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
    const page = this.closest(pageSelector)
    loadIssueMarkdown(page).then(() => navigator.clipboard.writeText(issueMarkdown))
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

function troubleshootingButtonPrevItemsClick(event) {
    const page = this.closest(pageSelector)
    getItemPage(page, Math.max(0, itemPageStart - itemPageSize))
}

function troubleshootingButtonNextItemsClick(event) {
    const page = this.closest(pageSelector)
    getItemPage(page, itemPageStart + itemPageSize)
}

function troubleshootingItemSearchInput(event) {
    const page = this.closest(pageSelector)
    const nextSearch = this.value.trim()
    clearItemSearchTimer()
    itemSearchTimer = window.setTimeout(function () {
        itemSearchTimer = null
        if (itemSearch === nextSearch) {
            updateItemControls(page)
            return
        }

        itemSearch = nextSearch
        itemPageStart = 0
        getItemPage(page, 0)
        updateItemControls(page)
    }, 550)
}

function troubleshootingButtonClearItemSearchClick(event) {
    const page = this.closest(pageSelector)
    clearItemSearchTimer()
    page.querySelector('#TroubleshootingItemSearch').value = ''
    itemSearch = ''
    itemPageStart = 0
    getItemPage(page, 0)
    updateItemControls(page)
}

function troubleshootingReportSourceToggle(event) {
    if (!this.open) {
        return
    }

    const page = this.closest(pageSelector)
    loadIssueMarkdown(page)
}

function troubleshootingButtonDownloadFullReportClick(event) {
    if (!reportId) {
        return
    }

    const button = this
    button.disabled = true
    Dashboard.showLoadingMsg()
    ApiClient.fetch({
        url: ApiClient.getUrl('MediaCleaner/ReportIssueMarkdown', { reportId: reportId }),
        type: 'GET',
        dataType: 'text',
    }).then(readTextResponse).then(markdown => {
        saveTextFile(markdown, `MediaCleaner-troubleshooting-${formatDownloadTimestamp(new Date())}.md`, 'text/markdown;charset=utf-8')
    }).catch(function (error) {
        console.log('Error downloading troubleshooting report', error)
        Dashboard.alert('Could not download the full Markdown report')
    }).then(function () {
        button.disabled = false
        updateItemControls(button.closest(pageSelector))
        Dashboard.hideLoadingMsg()
    })
}

function loadIssueMarkdown(page) {
    if (issueMarkdownLoaded) {
        return Promise.resolve(issueMarkdown)
    }

    if (issueMarkdownLoading) {
        return issueMarkdownLoading
    }

    if (!reportId) {
        return Promise.reject(new Error('No troubleshooting report is loaded'))
    }

    const log = page.querySelector(logTextareaSelector)
    log.value = 'Loading GitHub issue source...'
    issueMarkdownLoading = ApiClient.fetch({
        url: ApiClient.getUrl('MediaCleaner/ReportIssueSource', { reportId: reportId }),
    }).then(normalizeReportResponse).then(function (result) {
        issueMarkdown = result.issueMarkdown || result.IssueMarkdown || ''
        issueMarkdownLoaded = true
        issueMarkdownLoading = null
        log.value = issueMarkdown
        return issueMarkdown
    }).catch(function (error) {
        issueMarkdownLoading = null
        log.value = ''
        console.log(error)
        Dashboard.alert('Could not load the GitHub issue report')
        throw error
    })

    return issueMarkdownLoading
}

function clearItemSearchTimer() {
    if (itemSearchTimer !== null) {
        window.clearTimeout(itemSearchTimer)
        itemSearchTimer = null
    }
}

function readTextResponse(result) {
    if (!result) return ''
    if (typeof result === 'string') return result
    if (typeof Blob !== 'undefined' && result instanceof Blob) return result.text()
    if (typeof result.text === 'function') return result.text()
    return String(result)
}

function saveTextFile(text, fileName, contentType) {
    const blob = new Blob([text], { type: contentType })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    link.style.display = 'none'
    document.body.appendChild(link)
    link.click()
    link.remove()
    window.setTimeout(() => URL.revokeObjectURL(url), 0)
}

function formatDownloadTimestamp(date) {
    const pad = value => String(value).padStart(2, '0')
    return [
        date.getFullYear(),
        pad(date.getMonth() + 1),
        pad(date.getDate()),
        '-',
        pad(date.getHours()),
        pad(date.getMinutes()),
        pad(date.getSeconds()),
    ].join('')
}
