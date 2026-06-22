export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })

    view.querySelector('#MediaCleanerAdvancedForm').addEventListener('submit', function (e) {
        import(commonsUrl).then(onFormSubmit.bind(this))
        e.preventDefault()
        return false
    })

    view.querySelector('#MediaCleanerAllowDeleteIfPlayedBeforeAdded').addEventListener('change', function () {
        const page = this.closest('#MediaCleanerAdvancedPage')
        readSettings(page)
        updateSaveStatus(page)
    })

    view.querySelector('#MediaCleanerPreviewTagRename').addEventListener('click', function () {
        previewTagRename(this.closest('#MediaCleanerAdvancedPage'))
    })

    view.querySelector('#MediaCleanerRenameTag').addEventListener('click', function () {
        renameTag(this.closest('#MediaCleanerAdvancedPage'))
    })

    view.querySelectorAll('#MediaCleanerOldTag, #MediaCleanerNewTag').forEach(input => {
        input.addEventListener('input', function () {
            invalidateTagRenamePreview(this.closest('#MediaCleanerAdvancedPage'), true)
        })
    })
}

function onViewShow(commons) {
    const page = this
    commons.setTabs('MediaCleaner', commons.TabAdvanced, commons.getTabs)
    Dashboard.showLoadingMsg()

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        page._mediaCleanerAllowDeleteIfPlayedBeforeAdded = config.AllowDeleteIfPlayedBeforeAdded === true
        renderSettings(page)
        page._savedSettingsSnapshot = settingsSnapshot(page)
        updateSaveStatus(page)
        Dashboard.hideLoadingMsg()
    })
}

function onFormSubmit(commons) {
    const form = this
    const page = form.closest('#MediaCleanerAdvancedPage')
    Dashboard.showLoadingMsg()

    readSettings(page)

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        config.AllowDeleteIfPlayedBeforeAdded = page._mediaCleanerAllowDeleteIfPlayedBeforeAdded === true

        ApiClient.updatePluginConfiguration(commons.pluginId, config).then(result => {
            page._savedSettingsSnapshot = settingsSnapshot(page)
            updateSaveStatus(page)
            Dashboard.processPluginConfigurationUpdateResult(result)
        })
    })
}

function renderSettings(page) {
    const input = page.querySelector('[data-field="AllowDeleteIfPlayedBeforeAdded"]')
    if (!input) return
    input.checked = page._mediaCleanerAllowDeleteIfPlayedBeforeAdded === true
}

function readSettings(page) {
    page._mediaCleanerAllowDeleteIfPlayedBeforeAdded = readChecked(page, 'AllowDeleteIfPlayedBeforeAdded')
}

function settingsSnapshot(page) {
    return JSON.stringify({
        AllowDeleteIfPlayedBeforeAdded: page._mediaCleanerAllowDeleteIfPlayedBeforeAdded === true,
    })
}

function updateSaveStatus(page) {
    const status = page.querySelector('#MediaCleanerAdvancedSaveStatus')
    if (!status) return
    status.classList.toggle('hide', settingsSnapshot(page) === page._savedSettingsSnapshot)
}

function readChecked(page, field) {
    const input = page.querySelector(`[data-field="${field}"]`)
    return input ? input.checked === true : false
}

function previewTagRename(page) {
    const request = readTagRenameRequest(page)
    if (!request) return

    Dashboard.showLoadingMsg()
    tagRenameFetch('PreviewTagReplacement', request).then(report => {
        page._mediaCleanerTagRenamePreview = {
            key: tagRenameKey(request),
            updatedCount: numberValue(report.UpdatedCount ?? report.updatedCount),
            skippedCount: numberValue(report.SkippedCount ?? report.skippedCount),
            errorCount: numberValue(report.ErrorCount ?? report.errorCount),
            totalProcessed: numberValue(report.TotalProcessed ?? report.totalProcessed),
        }
        renderTagRenameResult(page, `Preview: ${page._mediaCleanerTagRenamePreview.updatedCount} item(s) will be updated; ${page._mediaCleanerTagRenamePreview.skippedCount} skipped.`)
        updateTagRenameButton(page)
        Dashboard.hideLoadingMsg()
    }).catch(error => {
        console.error('Error previewing tag rename:', error)
        page._mediaCleanerTagRenamePreview = null
        renderTagRenameResult(page, 'Could not preview the tag rename.')
        updateTagRenameButton(page)
        Dashboard.hideLoadingMsg()
    })
}

function renameTag(page) {
    const request = readTagRenameRequest(page)
    if (!request) return

    const preview = page._mediaCleanerTagRenamePreview
    if (!preview || preview.key !== tagRenameKey(request)) {
        renderTagRenameResult(page, 'Preview this rename before applying it.')
        updateTagRenameButton(page)
        return
    }

    if (!window.confirm(`Rename tag "${request.OldTag}" to "${request.NewTag}" on ${preview.updatedCount} item(s)? Rules are not changed automatically.`)) {
        return
    }

    Dashboard.showLoadingMsg()
    tagRenameFetch('ReplaceTag', request).then(report => {
        const updatedCount = numberValue(report.UpdatedCount ?? report.updatedCount)
        const skippedCount = numberValue(report.SkippedCount ?? report.skippedCount)
        const errorCount = numberValue(report.ErrorCount ?? report.errorCount)
        page._mediaCleanerTagRenamePreview = null
        renderTagRenameResult(page, `Rename complete: ${updatedCount} updated, ${skippedCount} skipped, ${errorCount} errors.`)
        updateTagRenameButton(page)
        Dashboard.hideLoadingMsg()
    }).catch(error => {
        console.error('Error renaming tag:', error)
        renderTagRenameResult(page, 'Could not rename the tag.')
        updateTagRenameButton(page)
        Dashboard.hideLoadingMsg()
    })
}

function readTagRenameRequest(page) {
    const oldTag = page.querySelector('#MediaCleanerOldTag').value.trim()
    const newTag = page.querySelector('#MediaCleanerNewTag').value.trim()
    if (!oldTag || !newTag) {
        renderTagRenameResult(page, 'Enter both old and new tag names.')
        invalidateTagRenamePreview(page)
        return null
    }

    if (oldTag.toLowerCase() === newTag.toLowerCase()) {
        renderTagRenameResult(page, 'Old and new tag names must be different.')
        invalidateTagRenamePreview(page)
        return null
    }

    return { OldTag: oldTag, NewTag: newTag }
}

function tagRenameFetch(endpoint, request) {
    return ApiClient.fetch({
        type: 'POST',
        url: ApiClient.getUrl(`MediaCleaner/${endpoint}`),
        data: JSON.stringify(request),
        contentType: 'application/json',
        dataType: 'json',
    }).then(normalizeTagRenameResponse)
}

function normalizeTagRenameResponse(result) {
    if (!result) return {}
    if (typeof result === 'string') return JSON.parse(result)
    if (typeof result.json === 'function') return result.json()
    if (typeof result.text === 'function') return result.text().then(text => JSON.parse(text))
    return result
}

function invalidateTagRenamePreview(page, clearResult = false) {
    page._mediaCleanerTagRenamePreview = null
    if (clearResult) renderTagRenameResult(page, '')
    updateTagRenameButton(page)
}

function updateTagRenameButton(page) {
    const button = page.querySelector('#MediaCleanerRenameTag')
    if (!button) return
    const request = readCurrentTagRenameRequest(page)
    const preview = page._mediaCleanerTagRenamePreview
    button.disabled = !request || !preview || preview.key !== tagRenameKey(request) || preview.updatedCount <= 0
}

function readCurrentTagRenameRequest(page) {
    const oldTag = page.querySelector('#MediaCleanerOldTag').value.trim()
    const newTag = page.querySelector('#MediaCleanerNewTag').value.trim()
    if (!oldTag || !newTag || oldTag.toLowerCase() === newTag.toLowerCase()) return null
    return { OldTag: oldTag, NewTag: newTag }
}

function renderTagRenameResult(page, text) {
    const result = page.querySelector('#MediaCleanerTagRenameResult')
    if (!result) return
    result.textContent = text
    result.classList.toggle('hide', !text)
}

function tagRenameKey(request) {
    return `${request.OldTag.toLowerCase()}\n${request.NewTag.toLowerCase()}`
}

function numberValue(value) {
    const number = Number(value)
    return Number.isFinite(number) ? number : 0
}
