const mediaKinds = ['Movie', 'Episode', 'Video', 'Audio', 'AudioBook']
const allMediaKind = 'All'
const triggerKinds = ['Played', 'NotPlayed', 'AddedAge']
const conditionRegistry = [
    {
        id: 'favorites',
        label: 'Favorites',
        isActive: rule => rule.Filters.FavoriteFilter !== 'Ignore',
        activate: rule => {
            rule.Filters.FavoriteFilter = 'NotFavoriteByAnyUser'
            rule.Filters.FavoriteUsersMode = 'Ignore'
            rule.Filters.FavoriteUserIds = []
        },
        clear: rule => {
            rule.Filters.FavoriteFilter = 'Ignore'
            rule.Filters.FavoriteUsersMode = 'Ignore'
            rule.Filters.FavoriteUserIds = []
        },
        summary: (rule, page) => favoriteConditionSummary(rule.Filters, favoriteUsersScopeForPage(page, rule)),
        render: (rule, page) => favoriteFilterHtml(rule.Filters, page._mediaCleanerUsers, favoriteUsersScopeForPage(page, rule)),
        read: (card, rule, page) => readFavoriteFilter(card, rule, page),
    },
    {
        id: 'locations',
        label: 'Locations',
        isActive: rule => rule.Filters.Locations.length > 0,
        activate: rule => { rule.Filters.LocationsMode = 'Include' },
        clear: rule => {
            rule.Filters.Locations = []
            rule.Filters.LocationsMode = 'Exclude'
        },
        summary: rule => rule.Filters.Locations.length > 0
            ? `${rule.Filters.LocationsMode === 'Include' ? 'Only use' : 'Exclude'} ${rule.Filters.Locations.length} selected location${rule.Filters.Locations.length === 1 ? '' : 's'}`
            : 'Choose locations',
        render: (rule, page) => locationFilterHtml(rule.Filters, page._mediaCleanerLocations),
        read: (card, rule) => {
            rule.Filters.LocationsMode = readField(card, 'LocationsMode', 'Include')
            rule.Filters.Locations = readMultiValue(card, 'Locations')
        },
    },
    {
        id: 'tags',
        label: 'Tags',
        isActive: rule => rule.Filters.EnableTagFilter === true,
        activate: rule => {
            rule.Filters.EnableTagFilter = true
            rule.Filters.TagFilterMode = 'Inclusion'
        },
        clear: rule => {
            rule.Filters.EnableTagFilter = false
            rule.Filters.TagFilterMode = 'Exclusion'
            rule.Filters.Tags = []
        },
        summary: rule => rule.Filters.Tags.length > 0
            ? `${rule.Filters.TagFilterMode === 'Inclusion' ? 'Require any tag' : 'Exclude any tag'}: ${rule.Filters.Tags.join(', ')}`
            : 'Choose tags',
        render: rule => tagFilterHtml(rule.Filters),
        read: (card, rule) => {
            rule.Filters.EnableTagFilter = true
            rule.Filters.TagFilterMode = readField(card, 'TagFilterMode', 'Inclusion')
            rule.Filters.Tags = lines(readText(card, 'Tags'))
        },
    },
]

export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })

    view.querySelector('#MediaCleanerConfigForm').addEventListener('submit', function (e) {
        import(commonsUrl).then(onFormSubmit.bind(this))
        e.preventDefault()
        return false
    })

    view.querySelector('#MediaCleanerScheduledTasksLink').addEventListener('click', function (e) {
        e.preventDefault()
        openScheduledTasks()
    })

    view.querySelector('#MediaCleanerBackupConfig').addEventListener('click', function (e) {
        e.preventDefault()
        downloadConfigBackup(e.currentTarget)
    })

}

function onViewShow(commons) {
    const page = this
    commons.setTabs('MediaCleaner', commons.TabGeneral, commons.getTabs)
    Dashboard.showLoadingMsg()

    page._mediaCleanerRules = []
    page._expandedRuleId = null
    page._mediaCleanerUsers = []
    page._mediaCleanerLocations = []
    page._activeConditionsByRuleId = {}
    page._favoriteScopesByRuleId = {}
    page._playbackScopesByRuleId = {}
    page._mediaCleanerStatus = null
    page._mediaCleanerMigrationReviewRequired = false
    page._savedRulesSnapshot = rulesSnapshot([])

    Promise.all([
        ApiClient.getPluginConfiguration(commons.pluginId),
        ApiClient.getUsers().catch(() => []),
        ApiClient.getVirtualFolders().catch(() => []),
        getMediaCleanerStatus().catch(error => {
            console.warn('[MediaCleaner] Could not load cleanup status', error)
            return null
        }),
    ]).then(([config, users, virtualFolders, status]) => {
        page._mediaCleanerUsers = normalizeUsers(users)
        page._mediaCleanerLocations = normalizeLocations(virtualFolders)
        page._mediaCleanerRules = normalizeRules(config)
        page._mediaCleanerStatus = status
        page._mediaCleanerMigrationReviewRequired = Number(config.ConfigVersion) < 2 && page._mediaCleanerRules.length > 0
        page._expandedRuleId = null
        page._savedRulesSnapshot = rulesSnapshot(page._mediaCleanerRules)
        updateMigrationReview(page)
        renderRules(page)
        renderOverview(page)
        Dashboard.hideLoadingMsg()
    })
}

function bindAddRuleButtons(page, list) {
    list.querySelectorAll('[data-add-rule]').forEach(button => {
        button.addEventListener('click', () => {
            const actionKind = button.dataset.addRule
            syncExpandedRule(page)
            const rule = defaultRule(actionKind)
            page._mediaCleanerRules.push(rule)
            page._expandedRuleId = rule.Id
            renderRules(page)
        })
    })
}

function onFormSubmit(commons) {
    const form = this
    const page = form.closest('#MediaCleanerConfigPage')
    Dashboard.showLoadingMsg()

    syncExpandedRule(page)

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        config.ConfigVersion = 2
        config.Rules = page._mediaCleanerRules.map(normalizeRule)

        ApiClient.updatePluginConfiguration(commons.pluginId, config).then(result => {
            page._mediaCleanerRules = config.Rules
            page._mediaCleanerMigrationReviewRequired = false
            page._savedRulesSnapshot = rulesSnapshot(page._mediaCleanerRules)
            updateMigrationReview(page)
            updateSaveStatus(page)
            Dashboard.processPluginConfigurationUpdateResult(result)
        })
    })
}

function renderRules(page) {
    const list = page.querySelector('#RulesList')
    list.innerHTML = [
        renderRuleSection(page, 'Cleanup rules', 'Delete media after it meets the rule conditions.', 'Delete', 'No cleanup rules.'),
        renderRuleSection(page, 'Protection rules', 'Exclude matching media from deletion by any cleanup rule.', 'Protect', 'No protection rules.'),
    ].join('')

    bindAddRuleButtons(page, list)

    list.querySelectorAll('[data-command]').forEach(button => {
        button.addEventListener('click', event => {
            event.preventDefault()
            event.stopPropagation()
            handleCommand(page, event.currentTarget).catch(error => console.error('[MediaCleaner] Rule command failed', error))
        })
    })

    list.querySelectorAll('[data-expand-rule]').forEach(element => {
        const expand = () => {
            const card = element.closest('[data-index]')
            syncExpandedRule(page)
            page._expandedRuleId = card.dataset.ruleId
            renderRules(page)
        }
        element.addEventListener('click', expand)
        element.addEventListener('keydown', event => {
            if (event.key !== 'Enter' && event.key !== ' ') return
            event.preventDefault()
            expand()
        })
    })

    list.querySelectorAll('[data-editor] input, [data-editor] select, [data-editor] textarea').forEach(input => {
        input.addEventListener('change', () => {
            syncExpandedRule(page)
            renderRules(page)
        })
        input.addEventListener('input', () => {
            syncExpandedRule(page)
            updateSaveStatus(page)
        })
    })

    if (page._ruleMenuOutsideHandler) document.removeEventListener('click', page._ruleMenuOutsideHandler)
    if (page._ruleMenuKeyHandler) document.removeEventListener('keydown', page._ruleMenuKeyHandler)
    page._ruleMenuOutsideHandler = event => {
        list.querySelectorAll('.mediaCleanerRuleMenuPanel:not(.hide)').forEach(menu => {
            if (!menu.parentNode.contains(event.target)) menu.classList.add('hide')
        })
    }
    page._ruleMenuKeyHandler = event => {
        if (event.key !== 'Escape') return
        list.querySelectorAll('.mediaCleanerRuleMenuPanel:not(.hide)').forEach(menu => menu.classList.add('hide'))
    }
    document.addEventListener('click', page._ruleMenuOutsideHandler)
    document.addEventListener('keydown', page._ruleMenuKeyHandler)
    updateSaveStatus(page)
    renderOverview(page)
}

function renderOverview(page) {
    if (!page) return

    const activeCleanupRuleCount = countActiveCleanupRules(page._mediaCleanerRules)
    setOverviewValue(
        page,
        '#MediaCleanerRulesState',
        activeCleanupRuleCount > 0
            ? `${activeCleanupRuleCount} active cleanup rule${activeCleanupRuleCount === 1 ? '' : 's'}`
            : 'No active cleanup rules. Scheduled runs will not delete anything.',
        activeCleanupRuleCount === 0)

    const status = page._mediaCleanerStatus
    if (!status) {
        setOverviewValue(page, '#MediaCleanerNextRun', 'Could not load scheduled task status.', true)
        setOverviewValue(page, '#MediaCleanerTaskState', 'Unknown', true)
        setScheduledTasksLinkVisible(page, false)
        return
    }

    const scheduledTaskAvailableValue = readResponseValue(status, 'ScheduledTaskAvailable', 'scheduledTaskAvailable')
    const scheduledTaskAvailable = scheduledTaskAvailableValue !== false
    const scheduledTaskState = readResponseValue(status, 'ScheduledTaskState', 'scheduledTaskState')
    const nextRunUtc = readResponseValue(status, 'NextRunUtc', 'nextRunUtc')
    setScheduledTasksLinkVisible(page, scheduledTaskAvailableValue === false || !isValidDateValue(nextRunUtc))

    const taskState = !scheduledTaskAvailable
        ? 'Media Cleaner scheduled task is not registered.'
        : scheduledTaskState ? scheduledTaskState : 'Registered'
    setOverviewValue(page, '#MediaCleanerTaskState', taskState, !scheduledTaskAvailable)

    const nextRun = !scheduledTaskAvailable
        ? 'Not available'
        : formatNextRun(nextRunUtc)
    setOverviewValue(page, '#MediaCleanerNextRun', nextRun, !scheduledTaskAvailable || !nextRunUtc)
}

function setOverviewValue(page, selector, text, isWarning = false) {
    const element = page.querySelector(selector)
    if (!element) return
    element.textContent = text
    element.classList.toggle('mediaCleanerOverviewValue-warning', isWarning)
}

function setScheduledTasksLinkVisible(page, isVisible) {
    const button = page.querySelector('#MediaCleanerScheduledTasksLink')
    if (!button) return
    button.hidden = !isVisible
}

function countActiveCleanupRules(rules) {
    return (rules || [])
        .map(normalizeRule)
        .filter(rule => rule.Enabled !== false && isDeleteRule(rule) && numberValue(rule.Trigger.Days, -1) >= 0)
        .length
}

function formatNextRun(value) {
    if (!isValidDateValue(value)) {
        return 'No upcoming run found. Check triggers in Scheduled Tasks.'
    }

    return new Date(value).toLocaleString()
}

function isValidDateValue(value) {
    if (!value) {
        return false
    }

    return !Number.isNaN(new Date(value).getTime())
}

function getMediaCleanerStatus() {
    const request = {
        url: ApiClient.getUrl('MediaCleaner/Status'),
        type: 'GET',
    }

    return ApiClient.fetch(request).then(normalizeJsonResponse)
}

function downloadConfigBackup(button) {
    if (!button) return

    button.disabled = true
    Dashboard.showLoadingMsg()
    ApiClient.fetch({
        url: ApiClient.getUrl('MediaCleaner/ConfigBackup'),
        type: 'GET',
        dataType: 'text',
    }).then(readTextResponse).then(xml => {
        saveTextFile(xml, `MediaCleaner-config-backup-${formatBackupTimestamp(new Date())}.xml`, 'application/xml')
    }).catch(error => {
        console.error('[MediaCleaner] Could not download configuration backup', error)
        Dashboard.alert('Could not download the configuration backup')
    }).then(() => {
        button.disabled = false
        Dashboard.hideLoadingMsg()
    })
}

function readTextResponse(result) {
    if (!result) return ''
    if (typeof result === 'string') return result
    if (typeof Blob !== 'undefined' && result instanceof Blob) return result.text()
    if (typeof result.text === 'function') return result.text()
    if (result.documentElement && typeof XMLSerializer !== 'undefined') return new XMLSerializer().serializeToString(result)
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

function formatBackupTimestamp(date) {
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

function openScheduledTasks() {
    Dashboard.navigate('/dashboard/tasks', false)
}

function normalizeJsonResponse(result) {
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

function readResponseValue(response, pascalName, camelName) {
    if (!response) return undefined
    return Object.prototype.hasOwnProperty.call(response, pascalName) ? response[pascalName] : response[camelName]
}

function renderRuleSection(page, title, description, actionKind, emptyText) {
    const cards = []
    page._mediaCleanerRules.forEach((rule, index) => {
        const normalized = normalizeRule(rule)
        page._mediaCleanerRules[index] = normalized
        if (normalized.Actions.Kind !== actionKind) {
            return
        }

        cards.push(`
            <div class="paperList mediaCleanerRuleCard${normalized.Enabled === false ? ' mediaCleanerRuleCard-disabled' : ''}" data-index="${index}" data-rule-id="${escapeAttribute(normalized.Id)}">
                ${normalized.Id === page._expandedRuleId
                    ? renderRuleEditor(page, normalized, index)
                    : renderRuleSummary(normalized, index, page._mediaCleanerRules)}
            </div>`)
    })

    const isCleanup = actionKind === 'Delete'
    const addLabel = isCleanup ? 'Add cleanup rule' : 'Add protection rule'
    const addIcon = isCleanup ? 'add' : 'shield'
    const addClass = 'emby-button mediaCleanerTextButton'

    return `
        <section class="mediaCleanerRuleGroup">
            <div class="mediaCleanerRuleGroupHeader">
                <h3>${escapeHtml(title)}</h3>
                <p class="fieldDescription mediaCleanerRulesHint">${escapeHtml(description)}</p>
            </div>
            ${cards.length > 0 ? cards.join('') : `<p class="mediaCleanerEmptyState">${escapeHtml(emptyText)}</p>`}
            <div class="mediaCleanerAddRuleActions">
                <button is="emby-button" type="button" class="${addClass}" data-add-rule="${escapeAttribute(actionKind)}">
                    <span class="material-icons ${addIcon}" aria-hidden="true"></span>
                    <span>${escapeHtml(addLabel)}</span>
                </button>
            </div>
        </section>`
}

function rulesSnapshot(rules) {
    return JSON.stringify((rules || []).map(normalizeRule))
}

function updateSaveStatus(page) {
    const status = page.querySelector('#MediaCleanerSaveStatus')
    if (!status) return
    status.classList.toggle('hide', rulesSnapshot(page._mediaCleanerRules) === page._savedRulesSnapshot)
}

function updateMigrationReview(page) {
    const notice = page.querySelector('#MediaCleanerMigrationReview')
    if (!notice) return
    notice.classList.toggle('hide', page._mediaCleanerMigrationReviewRequired !== true)
}

async function handleCommand(page, button) {
    const card = button.closest('[data-index]')
    const index = Number(card.dataset.index)
    syncExpandedRule(page)

    const rules = page._mediaCleanerRules.map(normalizeRule)
    const command = button.dataset.command
    const scrollRuleId = command === 'edit' || command === 'done' ? card.dataset.ruleId : null
    if (command === 'menu') {
        toggleRuleMenu(button)
        return
    }

    if (command === 'edit') {
        page._expandedRuleId = rules[index].Id
    } else if (command === 'done') {
        page._expandedRuleId = null
    } else if (command === 'delete') {
        if (!window.confirm('Delete this rule?')) return
        const deleted = rules.splice(index, 1)[0]
        if (deleted && page._expandedRuleId === deleted.Id) {
            page._expandedRuleId = null
        }
    } else if (command === 'duplicate') {
        const copy = JSON.parse(JSON.stringify(rules[index]))
        copy.Id = createId()
        rules.splice(index + 1, 0, normalizeRule(copy))
        page._expandedRuleId = copy.Id
    } else if (command === 'add-condition') {
        const condition = getCondition(button.dataset.condition || readAddConditionValue(button))
        if (condition) {
            condition.activate(rules[index])
            rememberCondition(page, rules[index].Id, condition.id)
            page._expandedRuleId = rules[index].Id
        }
    } else if (command === 'remove-condition') {
        const condition = getCondition(button.dataset.condition)
        if (condition) {
            condition.clear(rules[index])
            forgetCondition(page, rules[index].Id, condition.id)
            page._expandedRuleId = rules[index].Id
        }
    } else if (command === 'up') {
        const targetIndex = findAdjacentRuleIndex(rules, index, -1)
        if (targetIndex >= 0) {
            const item = rules.splice(index, 1)[0]
            rules.splice(targetIndex, 0, item)
        }
    } else if (command === 'down') {
        const targetIndex = findAdjacentRuleIndex(rules, index, 1)
        if (targetIndex >= 0) {
            const item = rules.splice(index, 1)[0]
            rules.splice(targetIndex, 0, item)
        }
    }

    page._mediaCleanerRules = rules
    renderRules(page)
    scrollToRuleCard(page, scrollRuleId)
}

function scrollToRuleCard(page, ruleId) {
    if (!page || !ruleId) return

    const scroll = () => {
        const card = page.querySelector(`[data-rule-id="${cssEscape(ruleId)}"]`)
        if (!card) return

        const behavior = prefersReducedMotion() ? 'auto' : 'smooth'
        card.scrollIntoView({ behavior, block: 'start', inline: 'nearest' })
    }

    window.requestAnimationFrame(scroll)
}

function toggleRuleMenu(button) {
    const menu = button.parentNode.querySelector('.mediaCleanerRuleMenuPanel')
    if (!menu) return
    const shouldOpen = menu.classList.contains('hide')
    document.querySelectorAll('#MediaCleanerConfigPage .mediaCleanerRuleMenuPanel:not(.hide)').forEach(openMenu => openMenu.classList.add('hide'))
    if (shouldOpen) menu.classList.remove('hide')
}

function findAdjacentRuleIndex(rules, index, direction) {
    const actionKind = rules[index] && rules[index].Actions.Kind
    for (let candidate = index + direction; candidate >= 0 && candidate < rules.length; candidate += direction) {
        if (rules[candidate].Actions.Kind === actionKind) {
            return candidate
        }
    }

    return -1
}

function syncExpandedRule(page) {
    if (!page || !page._expandedRuleId) {
        return
    }

    const card = page.querySelector(`[data-rule-id="${cssEscape(page._expandedRuleId)}"]`)
    if (!card || !card.querySelector('[data-editor]')) {
        return
    }

    const index = Number(card.dataset.index)
    page._mediaCleanerRules[index] = readRuleEditor(page, card)
}

function readRuleEditor(page, card) {
    const baseRule = normalizeRule(page._mediaCleanerRules[Number(card.dataset.index)] || {})
    const mediaKindSelected = readField(card, 'MediaKind', 'Movie')
    const activeIds = Array.prototype.map.call(card.querySelectorAll('[data-condition-id]'), block => block.dataset.conditionId)

    baseRule.Id = readText(card, 'Id') || createId()
    baseRule.Enabled = readChecked(card, 'Enabled')
    baseRule.Trigger.Kind = readField(card, 'TriggerKind', 'Played')
    baseRule.Trigger.Days = numberValue(readText(card, 'Days'), 0)
    baseRule.Trigger.PlayedKeepKind = readField(card, 'PlayedKeepKind', 'AnyUser')
    baseRule.Filters.MediaKinds = mediaKindSelected === allMediaKind ? mediaKinds.slice() : [mediaKindSelected]
    readPlaybackSettings(card, baseRule, page)
    baseRule.Actions.Kind = readField(card, 'ActionKind', baseRule.Actions.Kind || 'Delete')
    baseRule.Actions.MarkAsUnplayed = isDeleteRule(baseRule) && baseRule.Trigger.Kind === 'Played' && readChecked(card, 'MarkAsUnplayed')

    for (const condition of conditionRegistry) {
        if (activeIds.includes(condition.id) && conditionAvailable(condition, baseRule)) {
            condition.read(card, baseRule, page)
        } else {
            condition.clear(baseRule)
        }
    }

    readDeletionBehavior(card, baseRule)

    rememberConditions(page, baseRule.Id, activeIds.filter(id => {
        const condition = getCondition(id)
        return condition && conditionAvailable(condition, baseRule)
    }))

    return normalizeRule(baseRule)
}
function readMultiValue(card, field) {
    const container = card.querySelector(`[data-field="${field}"]`)
    if (!container) {
        return []
    }

    if (container.matches('textarea')) {
        return lines(container.value)
    }

    return Array.prototype.map.call(container.querySelectorAll('input:checked'), input => input.dataset.value || input.value)
}

function readField(card, field, fallback) {
    const input = card.querySelector(`[data-field="${field}"]`)
    return input ? input.value : fallback
}

function readText(card, field) {
    const input = card.querySelector(`[data-field="${field}"]`)
    return input ? input.value : ''
}

function readChecked(card, field) {
    const input = card.querySelector(`[data-field="${field}"]`)
    return input ? input.checked === true : false
}

function readAddConditionValue(button) {
    const select = button.parentNode.querySelector('[data-add-condition-select]')
    return select ? select.value : ''
}

function renderRuleSummary(rule, index, rules) {
    const summary = readableRule(rule)
    const canMoveUp = findAdjacentRuleIndex(rules, index, -1) >= 0
    const canMoveDown = findAdjacentRuleIndex(rules, index, 1) >= 0
    return `
        <div class="mediaCleanerRuleSummary">
            <div class="mediaCleanerRuleMain" data-expand-rule="true" tabindex="0" role="button" title="Edit rule">
                <div class="mediaCleanerRuleTitleRow">
                    <h3 class="mediaCleanerRuleTitle">${escapeHtml(rule.Name)}</h3>
                    <span class="mediaCleanerStatus ${rule.Enabled === false ? 'mediaCleanerStatus-off' : 'mediaCleanerStatus-on'}">${rule.Enabled === false ? 'Disabled' : 'Enabled'}</span>
                </div>
                <p class="mediaCleanerRuleSentence">${escapeHtml(summary)}</p>
            </div>
            <div class="mediaCleanerRuleActions" aria-label="Rule actions">
                ${iconButton('edit', 'edit', 'Edit')}
                <div class="mediaCleanerRuleMenu">
                    ${iconButton('menu', 'more_vert', 'More actions')}
                    <div class="dialog actionsheet-not-fullscreen actionSheet mediaCleanerRuleMenuPanel hide" role="menu">
                        <div class="actionSheetContent">
                            <div class="actionSheetScroller scrollY">
                                ${menuCommandButton('up', 'keyboard_arrow_up', 'Move up', !canMoveUp)}
                                ${menuCommandButton('down', 'keyboard_arrow_down', 'Move down', !canMoveDown)}
                                ${menuCommandButton('duplicate', 'content_copy', 'Duplicate')}
                                ${menuCommandButton('delete', 'delete', 'Delete')}
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>`
}

function renderRuleEditor(page, rule, index) {
    const normalized = normalizeRule(rule)
    const activeConditions = activeConditionsFor(page, normalized)
    const availableConditions = conditionRegistry.filter(condition => !activeConditions.some(active => active.id === condition.id) && conditionAvailable(condition, normalized))
    const refineConditions = activeConditions
    const refineBlocks = renderConditionBlocks(refineConditions, normalized, page) || '<p class="mediaCleanerEmptyState">No additional filters.</p>'
    return `
        <div data-editor="true">
            <input type="hidden" data-field="Id" value="${escapeAttribute(normalized.Id)}" />
            <div class="mediaCleanerEditorHeader">
                <div class="checkboxContainer mediaCleanerEnabledToggle">
                    <label><input is="emby-checkbox" type="checkbox" data-field="Enabled" ${normalized.Enabled !== false ? 'checked="checked"' : ''} /><span>Enabled</span></label>
                </div>
            </div>

            ${sentenceBuilderHtml(normalized, activeConditions, page)}

            <div class="mediaCleanerPipeline">
                ${pipelineBandHtml('Match conditions', 'Define when media becomes eligible for this rule.', `
                    <div class="mediaCleanerBuilderGrid">
                        ${builderBlockHtml('Media', mediaSentence(normalized.Filters.MediaKinds), renderMediaBlock(normalized.Filters), false)}
                        ${builderBlockHtml(normalized.Trigger.Kind === 'AddedAge' ? 'Age' : 'Age and playback', triggerSentence(normalized.Trigger, normalized.Filters), renderTriggerBlock(normalized, page), false)}
                    </div>`)}
                ${pipelineBandHtml('Additional filters', 'Optionally limit matching by favorites, locations, or tags.', `
                    <div class="mediaCleanerBuilderGrid">
                        ${refineBlocks}
                    </div>
                    ${addConditionHtml(availableConditions)}`)}
                ${isDeleteRule(normalized) ? pipelineBandHtml('Deletion behavior', outcomeDescription(normalized), `
                    <div class="mediaCleanerBuilderGrid">
                        ${builderBlockHtml('Delete matching media', deletionBehaviorSummary(normalized), renderDeletionBehavior(normalized), false)}
                    </div>`) : ''}
            </div>

            <div class="mediaCleanerEditorActions">
                <button is="emby-button" type="button" class="raised emby-button mediaCleanerTextButton" data-command="done"><span>Done</span></button>
            </div>
        </div>`
}

function pipelineBandHtml(title, description, content) {
    return `
        <section class="mediaCleanerPipelineBand">
            <div class="mediaCleanerPipelineBandHeader">
                <h3>${escapeHtml(title)}</h3>
                <p>${escapeHtml(description)}</p>
            </div>
            ${content}
        </section>`
}

function renderConditionBlocks(conditions, rule, page) {
    return conditions.map(condition => builderBlockHtml(condition.label, condition.summary(rule, page), condition.render(rule, page), true, condition.id)).join('')
}
function sentenceBuilderHtml(rule, activeConditions, page) {
    return `
        <div class="mediaCleanerSentenceBuilder" aria-label="Rule sentence">
            <span class="mediaCleanerSentenceWord">If</span>
            ${sentencePillHtml(mediaLabel(rule.Filters.MediaKinds))}
            ${sentencePillHtml(triggerLabel(rule.Trigger))}
            ${activeConditions.map(condition => conditionPillHtml(condition.label, condition.summary(rule, page), condition.id)).join('')}
            <span class="mediaCleanerSentenceWord">then</span>
            ${sentencePillHtml(actionPillLabel(rule), rule.Actions.Kind === 'Protect' ? 'mediaCleanerSentencePill-protect' : 'mediaCleanerSentencePill-delete')}
        </div>`
}

function sentencePillHtml(text, className = '') {
    return `<span class="mediaCleanerSentencePill ${className}">${escapeHtml(text)}</span>`
}

function conditionPillHtml(label, summary, id) {
    return `<span class="mediaCleanerSentencePill mediaCleanerConditionPill" title="${escapeAttribute(summary)}">${escapeHtml(label)}</span>`
}

function builderBlockHtml(title, summary, controls, removable, conditionId) {
    return `
        <section class="mediaCleanerBuilderBlock" ${conditionId ? `data-condition-id="${escapeAttribute(conditionId)}"` : ''}>
            <div class="mediaCleanerBuilderBlockHeader">
                <h3>${escapeHtml(title)}</h3>
                <p>${escapeHtml(summary)}</p>
            </div>
            <div class="mediaCleanerBuilderControls">${controls}</div>
            ${removable ? iconButton('remove-condition', 'close', `Remove ${title}`, { condition: conditionId, className: 'mediaCleanerConditionRemove' }) : ''}
        </section>`
}

function renderTriggerBlock(rule, page) {
    const trigger = rule.Trigger
    const playback = trigger.Kind === 'AddedAge' ? '' : playbackSettingsHtml(rule, page)
    const zeroDayWarning = isDeleteRule(rule) && trigger.Days === 0
        ? warningHtml('This cleanup rule can delete matching media immediately. Review the conditions carefully before saving.')
        : ''
    return `
        ${selectHtml('TriggerKind', 'Match media based on', trigger.Kind, triggerKinds)}
        ${triggerDescriptionHtml(trigger.Kind)}
        ${numberHtml('Days', triggerDaysLabel(trigger.Kind), trigger.Days, 0)}
        ${zeroDayWarning}
        ${trigger.Kind === 'Played' ? selectHtml('PlayedKeepKind', 'Playback requirement', trigger.PlayedKeepKind, ['AnyUser', 'AnyUserRolling', 'AllUsers']) : ''}
        ${playback}`
}

function renderMediaBlock(filters) {
    return selectHtml('MediaKind', 'Media type', mediaKindValue(filters.MediaKinds), [allMediaKind, ...mediaKinds])
}

function outcomeDescription() {
    return 'Choose how matching media is deleted.'
}

function addConditionHtml(conditions) {
    if (conditions.length === 0) {
        return ''
    }

    return `
        <div class="mediaCleanerAddCondition">
            <h3>Add condition</h3>
            <div class="mediaCleanerConditionPicker">
                ${conditions.map(condition => `<button is="emby-button" type="button" class="emby-button mediaCleanerTextButton mediaCleanerConditionChoice" data-command="add-condition" data-condition="${escapeAttribute(condition.id)}"><span class="material-icons add" aria-hidden="true"></span><span>${escapeHtml(condition.label)}</span></button>`).join('')}
            </div>
        </div>`
}

function activeConditionsFor(page, rule) {
    const remembered = (page._activeConditionsByRuleId && page._activeConditionsByRuleId[rule.Id]) || []
    return conditionRegistry.filter(condition => conditionAvailable(condition, rule) && (condition.isActive(rule) || remembered.includes(condition.id)))
}

function conditionAvailable(condition, rule) {
    return !condition.canUse || condition.canUse(rule)
}

function getCondition(id) {
    return conditionRegistry.find(condition => condition.id === id)
}

function rememberCondition(page, ruleId, conditionId) {
    page._activeConditionsByRuleId = page._activeConditionsByRuleId || {}
    rememberConditions(page, ruleId, unique([...(page._activeConditionsByRuleId[ruleId] || []), conditionId]))
}

function forgetCondition(page, ruleId, conditionId) {
    page._activeConditionsByRuleId = page._activeConditionsByRuleId || {}
    rememberConditions(page, ruleId, (page._activeConditionsByRuleId[ruleId] || []).filter(id => id !== conditionId))
    if (conditionId === 'favorites' && page._favoriteScopesByRuleId) {
        delete page._favoriteScopesByRuleId[ruleId]
    }
}

function rememberConditions(page, ruleId, conditionIds) {
    page._activeConditionsByRuleId = page._activeConditionsByRuleId || {}
    page._activeConditionsByRuleId[ruleId] = unique(conditionIds)
}

function playbackSettingsHtml(rule, page) {
    const filters = rule.Filters
    const scope = playbackUsersScopeForPage(page, rule)
    const users = scope === 'AllUsers'
        ? ''
        : userListHtml('UserIds', page._mediaCleanerUsers, filters.UserIds, 'Selected users')
    const warning = scope === 'SelectedOnly' && filters.UserIds.length === 0
        ? warningHtml('Select at least one user. With an empty selection, this rule will not match any media.')
        : scope === 'AllExceptSelected' && filters.UserIds.length === 0
            ? warningHtml('Select at least one user to exclude. An empty exclusion is equivalent to all users.')
            : ''
    const hasHistoryWindow = rule.Trigger.CountAsNotPlayedAfter >= 0
    const historyWindow = hasHistoryWindow
        ? numberHtml('CountAsNotPlayedAfter', 'Ignore playback older than (days)', rule.Trigger.CountAsNotPlayedAfter, 0)
        : ''
    return selectHtml('PlaybackUsersScope', 'Users included in playback checks', scope, ['AllUsers', 'SelectedOnly', 'AllExceptSelected'])
        + users
        + warning
        + `<div class="mediaCleanerAdvanced"><div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" data-field="EnablePlaybackHistoryWindow" ${hasHistoryWindow ? 'checked="checked"' : ''} /><span>Ignore older playback history</span></label></div>${historyWindow}</div>`
}

function playbackUsersScope(filters) {
    if (filters.UsersMode === 'Acknowledge') return 'SelectedOnly'
    return filters.UserIds && filters.UserIds.length > 0 ? 'AllExceptSelected' : 'AllUsers'
}

function playbackUsersScopeForPage(page, rule) {
    const remembered = page && page._playbackScopesByRuleId && page._playbackScopesByRuleId[rule.Id]
    return remembered || playbackUsersScope(rule.Filters)
}

function readPlaybackSettings(card, rule, page) {
    if (rule.Trigger.Kind === 'AddedAge') {
        rule.Filters.UserIds = []
        rule.Filters.UsersMode = 'Ignore'
        rule.Trigger.CountAsNotPlayedAfter = -1
        if (page._playbackScopesByRuleId) delete page._playbackScopesByRuleId[rule.Id]
        return
    }

    const scope = readField(card, 'PlaybackUsersScope', 'AllUsers')
    page._playbackScopesByRuleId = page._playbackScopesByRuleId || {}
    page._playbackScopesByRuleId[rule.Id] = scope
    rule.Filters.UserIds = scope === 'AllUsers' ? [] : readMultiValue(card, 'UserIds')
    rule.Filters.UsersMode = scope === 'SelectedOnly' ? 'Acknowledge' : 'Ignore'
    rule.Trigger.CountAsNotPlayedAfter = readChecked(card, 'EnablePlaybackHistoryWindow')
        ? numberValue(readText(card, 'CountAsNotPlayedAfter'), 30)
        : -1
}

function renderDeletionBehavior(rule) {
    const episodeControls = isEpisodeOnly(rule)
        ? selectHtml('DeleteEpisodes', 'Episode deletion scope', rule.Filters.DeleteEpisodes, ['Episode', 'Season', 'Series', 'SeriesEnded'])
            + (['Episode', 'Season'].includes(rule.Filters.DeleteEpisodes)
                ? selectHtml('KeepSeriesKind', rule.Filters.DeleteEpisodes === 'Season' ? 'Season exception' : 'Episode exception', rule.Filters.KeepSeriesKind, ['None', 'First', 'Last'])
                : '')
        : ''
    const markUnplayed = rule.Trigger.Kind === 'Played'
        ? `<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" data-field="MarkAsUnplayed" ${rule.Actions.MarkAsUnplayed ? 'checked="checked"' : ''} /><span>Mark as unplayed when deleting</span></label></div>`
        : ''
    const controls = episodeControls + markUnplayed
    return `<input type="hidden" data-field="ActionKind" value="Delete" />${controls || '<p class="mediaCleanerEmptyState">Matching media will be deleted.</p>'}`
}

function readDeletionBehavior(card, rule) {
    if (!isDeleteRule(rule) || !isEpisodeOnly(rule)) {
        rule.Filters.DeleteEpisodes = 'Episode'
        rule.Filters.KeepSeriesKind = 'None'
        return
    }

    rule.Filters.DeleteEpisodes = readField(card, 'DeleteEpisodes', 'Episode')
    rule.Filters.KeepSeriesKind = ['Episode', 'Season'].includes(rule.Filters.DeleteEpisodes)
        ? readField(card, 'KeepSeriesKind', 'None')
        : 'None'
}

function deletionBehaviorSummary(rule) {
    const scope = isEpisodeOnly(rule)
        ? episodeScopeSentence(rule.Filters) || 'Delete matching episodes individually'
        : 'Delete matching media'
    return rule.Trigger.Kind === 'Played' && rule.Actions.MarkAsUnplayed ? `${scope}; mark deleted media as unplayed` : scope
}

function locationFilterHtml(filters, locations) {
    const warning = filters.Locations.length === 0
        ? warningHtml('Select at least one location. Until then, this filter does not restrict the rule.')
        : ''
    return selectHtml('LocationsMode', 'Location matching', filters.LocationsMode, ['Include', 'Exclude'])
        + locationListHtml(locations, filters.Locations)
        + warning
}

function tagFilterHtml(filters) {
    const warning = filters.Tags.length === 0
        ? warningHtml(filters.TagFilterMode === 'Inclusion'
            ? 'Add at least one tag. Requiring tags with an empty list makes this rule match no media.'
            : 'Add at least one tag. Until then, this filter does not restrict the rule.')
        : ''
    return selectHtml('TagFilterMode', 'Tag matching', filters.TagFilterMode, ['Inclusion', 'Exclusion'])
        + textListHtml('Tags', 'Tags', filters.Tags, 'Separate tags with commas. An item matches if it has any listed tag. Changing a rule tag only changes matching; use Advanced to rename existing Jellyfin tags.')
        + warning
}

function warningHtml(text) {
    return `<div class="mediaCleanerWarning" role="alert"><span class="material-icons warning" aria-hidden="true"></span><span>${escapeHtml(text)}</span></div>`
}

function favoriteFilterHtml(filters, users, scope) {
    const userList = scope === 'AllUsers'
        ? ''
        : userListHtml('FavoriteUserIds', users, filters.FavoriteUserIds, 'Selected users')
    const warning = scope === 'SelectedOnly' && filters.FavoriteUserIds.length === 0
        ? warningHtml('Select at least one user. Favorite checks need a non-empty selected-user scope.')
        : scope === 'AllExceptSelected' && filters.FavoriteUserIds.length === 0
            ? warningHtml('Select at least one user to exclude. An empty exclusion is equivalent to all users.')
            : ''
    return selectHtml('FavoriteFilter', 'Favorite requirement', filters.FavoriteFilter, ['FavoriteByAnyUser', 'FavoriteByAllUsers', 'NotFavoriteByAnyUser', 'NotFavoriteByAllUsers'])
        + selectHtml('FavoriteUsersScope', 'Users included in favorite checks', scope, ['AllUsers', 'SelectedOnly', 'AllExceptSelected'])
        + userList
        + warning
}

function favoriteUsersScope(filters) {
    if (filters.FavoriteUsersMode === 'Acknowledge') return 'SelectedOnly'
    return filters.FavoriteUserIds && filters.FavoriteUserIds.length > 0 ? 'AllExceptSelected' : 'AllUsers'
}

function favoriteUsersScopeForPage(page, rule) {
    const remembered = page && page._favoriteScopesByRuleId && page._favoriteScopesByRuleId[rule.Id]
    return remembered || favoriteUsersScope(rule.Filters)
}

function readFavoriteFilter(card, rule, page) {
    const filters = rule.Filters
    filters.FavoriteFilter = readField(card, 'FavoriteFilter', 'NotFavoriteByAnyUser')
    const scope = readField(card, 'FavoriteUsersScope', 'AllUsers')
    page._favoriteScopesByRuleId = page._favoriteScopesByRuleId || {}
    page._favoriteScopesByRuleId[rule.Id] = scope
    filters.FavoriteUserIds = scope === 'AllUsers' ? [] : readMultiValue(card, 'FavoriteUserIds')
    filters.FavoriteUsersMode = scope === 'SelectedOnly' ? 'Acknowledge' : 'Ignore'
}

function favoriteConditionSummary(filters, scope = favoriteUsersScope(filters)) {
    const requirement = favoriteSentence(filters.FavoriteFilter, scope) || 'any favorite state'
    return requirement.charAt(0).toUpperCase() + requirement.slice(1)
}

function triggerDescriptionHtml(kind) {
    const descriptions = {
        Played: 'Match media whose qualifying playback happened at least this many days ago. Playback before Date Added is ignored for safety unless advanced playback matching is enabled.',
        NotPlayed: 'Match media that was added at least this many days ago and has no qualifying playback. Playback before Date Added is treated as ambiguous and blocks matching unless advanced playback matching is enabled.',
        AddedAge: 'Match media that was added at least this many days ago, regardless of playback history. This uses Date Added.',
        AddedAge: 'Match media that was added at least this many days ago, regardless of playback history. This uses Date Added.',
    }
    return `<div class="fieldDescription mediaCleanerTriggerDescription">${escapeHtml(descriptions[kind] || '')}</div>`
}

function triggerDaysLabel(kind) {
    if (kind === 'Played') return 'Minimum days since playback'
    return 'Minimum days since added'
}

function episodeScopeWithPreservedItem(summary, filters, itemKind) {
    if (filters.KeepSeriesKind === 'First') return `${summary}, excluding the first ${itemKind}`
    if (filters.KeepSeriesKind === 'Last') return `${summary}, excluding the latest ${itemKind}`
    return summary
}

function generatedRuleName(rule) {
    const parts = [generatedMediaLabel(rule.Filters.MediaKinds), generatedTriggerLabel(rule.Trigger)]
    if (isDeleteRule(rule) && isEpisodeOnly(rule)) {
        parts.push(generatedEpisodeScopeLabel(rule.Filters.DeleteEpisodes))
    }

    return parts.join(' · ')
}

function generatedMediaLabel(kinds) {
    if (isAllMedia(kinds)) return 'All media'
    const label = pluralMediaKind(kinds[0])
    return label.charAt(0).toUpperCase() + label.slice(1)
}

function generatedTriggerLabel(trigger) {
    if (trigger.Kind === 'Played') return trigger.Days === 0 ? 'Played immediately' : `Played ${trigger.Days}d ago`
    if (trigger.Kind === 'NotPlayed') return trigger.Days === 0 ? 'Not played, added immediately' : `Not played, added ${trigger.Days}d ago`
    return trigger.Days === 0 ? 'Added immediately' : `Added ${trigger.Days}d ago`
}

function generatedEpisodeScopeLabel(scope) {
    if (scope === 'Season') return 'Complete seasons'
    if (scope === 'Series') return 'Complete series'
    if (scope === 'SeriesEnded') return 'Complete ended series'
    return 'Individual episodes'
}

function triggerLabel(trigger) {
    if (trigger.Kind === 'Played') {
        return trigger.Days === 0 ? 'eligible immediately after playback' : `played at least ${dayText(trigger.Days)} ago`
    }

    if (trigger.Kind === 'NotPlayed') {
        return trigger.Days === 0 ? 'not played; eligible immediately after being added' : `not played and added at least ${dayText(trigger.Days)} ago`
    }

    return trigger.Days === 0 ? 'eligible immediately after being added' : `added at least ${dayText(trigger.Days)} ago, regardless of playback`
}
function readableRule(rule) {
    const normalized = normalizeRule(rule)
    return `If ${mediaSentence(normalized.Filters.MediaKinds)} ${triggerSentence(normalized.Trigger, normalized.Filters)}${filterSentence(normalized.Filters)}, then ${actionSentence(normalized)}.`
}

function mediaSentence(kinds) {
    if (isAllMedia(kinds)) {
        return 'all media'
    }

    const labels = kinds.map(kind => pluralMediaKind(kind))
    if (labels.length === 1) {
        return labels[0]
    }

    if (labels.length === 2) {
        return `${labels[0]} or ${labels[1]}`
    }

    return `${labels.slice(0, -1).join(', ')}, or ${labels[labels.length - 1]}`
}

function triggerSentence(trigger, filters) {
    const scope = playbackUsersScope(filters)
    if (trigger.Kind === 'Played') {
        const audience = trigger.PlayedKeepKind === 'AllUsers'
            ? playbackEveryUserPhrase(scope)
            : playbackAtLeastOneUserPhrase(scope)
        if (trigger.Days === 0) {
            return trigger.PlayedKeepKind === 'AnyUserRolling'
                ? `become eligible immediately after the most recent playback by ${playbackAnyUserPhrase(scope)}`
                : `become eligible immediately after playback by ${audience}`
        }

        return trigger.PlayedKeepKind === 'AnyUserRolling'
            ? `had their most recent playback by ${playbackAnyUserPhrase(scope)} at least ${dayText(trigger.Days)} ago`
            : `were played by ${audience} at least ${dayText(trigger.Days)} ago`
    }

    if (trigger.Kind === 'NotPlayed') {
        const notPlayed = `were not played by ${playbackAnyUserPhrase(scope)}`
        return trigger.Days === 0
            ? `${notPlayed} and become eligible immediately after being added`
            : `were added at least ${dayText(trigger.Days)} ago and ${notPlayed}`
    }

    return trigger.Days === 0
        ? 'become eligible immediately after being added regardless of playback history'
        : `were added at least ${dayText(trigger.Days)} ago regardless of playback history`
}

function playbackAnyUserPhrase(scope) {
    if (scope === 'SelectedOnly') return 'any selected user'
    if (scope === 'AllExceptSelected') return 'any non-selected user'
    return 'any user'
}

function playbackAtLeastOneUserPhrase(scope) {
    if (scope === 'SelectedOnly') return 'at least one selected user'
    if (scope === 'AllExceptSelected') return 'at least one non-selected user'
    return 'at least one user'
}

function playbackEveryUserPhrase(scope) {
    if (scope === 'SelectedOnly') return 'every selected user'
    if (scope === 'AllExceptSelected') return 'every non-selected user'
    return 'every user'
}

function filterSentence(filters) {
    const parts = []

    const favorite = favoriteSentence(filters.FavoriteFilter, favoriteUsersScope(filters))
    if (favorite) {
        parts.push(favorite)
    }

    if (filters.Locations.length > 0) {
        parts.push(filters.LocationsMode === 'Include' ? 'only in selected locations' : 'outside selected locations')
    }

    if (filters.EnableTagFilter && filters.Tags.length > 0) {
        const tags = filters.Tags.map(tag => `"${tag}"`).join(', ')
        parts.push(filters.TagFilterMode === 'Inclusion' ? `with any tag ${tags}` : `without tags ${tags}`)
    }

    if (filters.MediaKinds.length === 1 && filters.MediaKinds[0] === 'Episode') {
        const scope = episodeScopeClause(filters)
        if (scope) {
            parts.push(scope)
        }
    }

    return parts.length > 0 ? `, ${parts.join(', ')}` : ''
}

function favoriteSentence(value, scope = 'AllUsers') {
    const atLeastOne = scope === 'SelectedOnly'
        ? 'at least one selected user'
        : scope === 'AllExceptSelected' ? 'at least one non-selected user' : 'at least one user'
    const every = scope === 'SelectedOnly'
        ? 'every selected user'
        : scope === 'AllExceptSelected' ? 'every non-selected user' : 'every user'
    const any = scope === 'SelectedOnly'
        ? 'any selected user'
        : scope === 'AllExceptSelected' ? 'any non-selected user' : 'any user'
    switch (value) {
        case 'FavoriteByAnyUser':
            return `favorited by ${atLeastOne}`
        case 'FavoriteByAllUsers':
            return `favorited by ${every}`
        case 'NotFavoriteByAnyUser':
            return `not favorited by ${any}`
        case 'NotFavoriteByAllUsers':
            return `not favorited by ${atLeastOne}`
        default:
            return ''
    }
}

function episodeScopeSentence(filters) {
    if (filters.DeleteEpisodes === 'Season') {
        return episodeScopeWithPreservedItem('Delete complete seasons when every episode matches', filters, 'season')
    }

    if (filters.DeleteEpisodes === 'Series') {
        return 'Delete complete series when every episode matches'
    }

    if (filters.DeleteEpisodes === 'SeriesEnded') {
        return 'Delete complete ended series when every episode matches'
    }

    if (filters.KeepSeriesKind === 'First') {
        return 'Delete matching episodes individually, except the first episode'
    }

    if (filters.KeepSeriesKind === 'Last') {
        return 'Delete matching episodes individually, except the latest episode'
    }

    return ''
}

function episodeScopeClause(filters) {
    if (filters.DeleteEpisodes === 'Season') {
        return episodeScopeWithPreservedItem('grouped into complete seasons where every episode matches', filters, 'season')
    }

    if (filters.DeleteEpisodes === 'Series') {
        return 'grouped into complete series where every episode matches'
    }

    if (filters.DeleteEpisodes === 'SeriesEnded') {
        return 'grouped into complete ended series where every episode matches'
    }

    if (filters.KeepSeriesKind === 'First') {
        return 'excluding the first episode in each series'
    }

    if (filters.KeepSeriesKind === 'Last') {
        return 'excluding the latest episode in each series'
    }

    return ''
}

function actionSentence(rule) {
    if (rule.Actions.Kind === 'Protect') {
        return 'exclude them from deletion'
    }

    if (isEpisodeOnly(rule)) {
        if (rule.Filters.DeleteEpisodes === 'Season') {
            return 'delete each complete season'
        }

        if (rule.Filters.DeleteEpisodes === 'Series' || rule.Filters.DeleteEpisodes === 'SeriesEnded') {
            return 'delete each complete series'
        }
    }

    return rule.Actions.MarkAsUnplayed ? 'delete them and mark them as unplayed' : 'delete them'
}

function actionPillLabel(rule) {
    return rule.Actions.Kind === 'Protect' ? 'Exclude from deletion' : actionLabel(rule)
}

function actionLabel(rule) {
    if (rule.Actions.Kind === 'Protect') {
        return 'Protection'
    }

    if (isEpisodeOnly(rule) && rule.Filters.DeleteEpisodes !== 'Episode') {
        return rule.Filters.DeleteEpisodes === 'SeriesEnded' ? 'Delete ended series' : `Delete complete ${labelFor(rule.Filters.DeleteEpisodes).toLowerCase()}`
    }

    return 'Delete'
}

function mediaLabel(kinds) {
    if (isAllMedia(kinds)) {
        return 'All media'
    }

    return kinds.map(labelFor).join(', ')
}

function pluralMediaKind(kind) {
    switch (kind) {
        case 'Movie':
            return 'movies'
        case 'Episode':
            return 'episodes'
        case 'Video':
            return 'videos'
        case 'Audio':
            return 'audio items'
        case 'AudioBook':
            return 'audiobooks'
        default:
            return `${labelFor(kind).toLowerCase()} items`
    }
}

function dayText(value) {
    const days = numberValue(value, 0)
    return `${days} ${days === 1 ? 'day' : 'days'}`
}

function normalizeUsers(users) {
    return (users || []).map(user => ({
        Id: user.Id,
        Name: user.Name || user.Username || user.Id,
    })).filter(user => user.Id)
}

function normalizeLocations(virtualFolders) {
    const locations = []
    for (const folder of virtualFolders || []) {
        for (const location of folder.Locations || []) {
            if (!locations.some(value => value.toLowerCase() === String(location).toLowerCase())) {
                locations.push(String(location))
            }
        }
    }

    return locations.sort((left, right) => left.localeCompare(right))
}

function normalizeRules(config) {
    if (Number(config.ConfigVersion) >= 2) {
        return Array.isArray(config.Rules) ? config.Rules.flatMap(expandMediaKinds) : []
    }

    if (Array.isArray(config.Rules) && config.Rules.length > 0) {
        return config.Rules.flatMap(expandMediaKinds)
    }

    const rules = []
    addLegacyRules(rules, config, 'Movies', 'Movie', config.KeepMoviesFor, config.KeepMoviesNotPlayedFor, config.KeepPlayedMovies, config.KeepFavoriteMovies, 'Episode', 'None')
    addLegacyRules(rules, config, 'Episodes', 'Episode', config.KeepEpisodesFor, config.KeepEpisodesNotPlayedFor, config.KeepPlayedEpisodes, config.KeepFavoriteEpisodes, config.DeleteEpisodes, config.KeepSeriesKind)
    addLegacyRules(rules, config, 'Videos', 'Video', config.KeepVideosFor, config.KeepVideosNotPlayedFor, config.KeepPlayedVideos, config.KeepFavoriteVideos, 'Episode', 'None')
    addLegacyRules(rules, config, 'Audio', 'Audio', config.KeepAudioFor, config.KeepAudioNotPlayedFor, config.KeepPlayedAudio, config.KeepFavoriteAudio, 'Episode', 'None')
    addLegacyRules(rules, config, 'Audiobooks', 'AudioBook', config.KeepAudioBooksFor, config.KeepAudioBooksNotPlayedFor, config.KeepPlayedAudioBooks, config.KeepFavoriteAudioBooks, 'Episode', 'None')
    return rules.length > 0 ? rules : []
}

function addLegacyRules(rules, config, label, mediaKind, playedDays, notPlayedDays, playedKeepKind, favoriteKeepKind, deleteEpisodes, keepSeriesKind) {
    if (Number(playedDays) >= 0) {
        rules.push(legacyRule(config, `${label} played`, mediaKind, 'Played', Number(playedDays), playedKeepKind, favoriteKeepKind, deleteEpisodes, keepSeriesKind))
    }

    if (Number(notPlayedDays) >= 0) {
        const rule = legacyRule(config, `${label} not played`, mediaKind, 'NotPlayed', Number(notPlayedDays), playedKeepKind, favoriteKeepKind, deleteEpisodes, keepSeriesKind)
        rule.Trigger.CountAsNotPlayedAfter = numberValue(config.CountAsNotPlayedAfter, -1)
        rules.push(rule)
    }
}

function legacyRule(config, name, mediaKind, triggerKind, days, playedKeepKind, favoriteKeepKind, deleteEpisodes, keepSeriesKind) {
    const tag = (config.TagFilterMode || 'Exclusion') === 'Exclusion' ? config.ExclusionTag : config.InclusionTag
    return normalizeRule({
        Id: createId(),
        Name: name,
        Enabled: true,
        Trigger: { Kind: triggerKind, Days: days, PlayedKeepKind: playedKeepKind || 'AnyUser', CountAsNotPlayedAfter: numberValue(config.CountAsNotPlayedAfter, -1) },
        Filters: {
            MediaKinds: [mediaKind],
            UserIds: config.UsersIgnorePlayed || [],
            UsersMode: config.UsersPlayedMode || 'Ignore',
            FavoriteUserIds: config.UsersIgnoreFavorited || [],
            FavoriteUsersMode: config.UsersFavoritedMode || 'Ignore',
            FavoriteFilter: mapFavoriteFilter(favoriteKeepKind),
            Locations: config.LocationsExcluded || [],
            LocationsMode: config.LocationsMode || 'Exclude',
            EnableTagFilter: config.EnableTagExclusion !== false,
            TagFilterMode: config.TagFilterMode || 'Exclusion',
            Tags: tag && String(tag).trim() ? [tag] : [],
            DeleteEpisodes: deleteEpisodes || 'Episode',
            KeepSeriesKind: keepSeriesKind || 'None',
        },
        Actions: { Kind: 'Delete', MarkAsUnplayed: config.MarkAsUnplayed === true },
    })
}

function defaultRule(actionKind = 'Delete') {
    return normalizeRule({
        Id: createId(),
        Enabled: true,
        Trigger: { Kind: 'Played', Days: 30, PlayedKeepKind: 'AnyUser', CountAsNotPlayedAfter: -1 },
        Filters: { MediaKinds: ['Movie'] },
        Actions: { Kind: actionKind, MarkAsUnplayed: false },
    })
}

function normalizeRule(rule) {
    const normalized = {
        Id: rule.Id || createId(),
        Name: '',
        Enabled: rule.Enabled !== false,
        Trigger: normalizeTrigger(rule.Trigger),
        Filters: normalizeFilters(rule.Filters),
        Actions: normalizeActions(rule.Actions),
    }

    if (isProtectRule(normalized)) {
        normalized.Filters.DeleteEpisodes = 'Episode'
        normalized.Filters.KeepSeriesKind = 'None'
        normalized.Actions.MarkAsUnplayed = false
    }

    if (!isEpisodeOnly(normalized)) {
        normalized.Filters.DeleteEpisodes = 'Episode'
        normalized.Filters.KeepSeriesKind = 'None'
    }

    normalized.Name = generatedRuleName(normalized)
    return normalized
}

function normalizeTrigger(trigger) {
    trigger = trigger || {}
    return {
        Kind: trigger.Kind || 'Played',
        Days: numberValue(trigger.Days, 30),
        PlayedKeepKind: trigger.PlayedKeepKind || 'AnyUser',
        CountAsNotPlayedAfter: numberValue(trigger.CountAsNotPlayedAfter, -1),
    }
}

function normalizeFilters(filters) {
    filters = filters || {}
    const normalizedMediaKinds = normalizeMediaKinds(filters.MediaKinds)
    return {
        MediaKinds: normalizedMediaKinds,
        UserIds: filters.UserIds || [],
        UsersMode: filters.UsersMode || 'Ignore',
        FavoriteUserIds: filters.FavoriteUserIds || [],
        FavoriteUsersMode: filters.FavoriteUsersMode || 'Ignore',
        FavoriteFilter: filters.FavoriteFilter || 'Ignore',
        Locations: filters.Locations || [],
        LocationsMode: filters.LocationsMode || 'Exclude',
        EnableTagFilter: filters.EnableTagFilter === true,
        TagFilterMode: filters.TagFilterMode || 'Exclusion',
        Tags: filters.Tags || [],
        DeleteEpisodes: filters.DeleteEpisodes || 'Episode',
        KeepSeriesKind: filters.KeepSeriesKind || 'None',
    }
}

function normalizeActions(actions) {
    actions = actions || {}
    const kind = actions.Kind === 'Protect' ? 'Protect' : 'Delete'
    return {
        Kind: kind,
        MarkAsUnplayed: kind === 'Delete' && actions.MarkAsUnplayed === true,
    }
}

function isProtectRule(rule) {
    return rule && rule.Actions && rule.Actions.Kind === 'Protect'
}

function isDeleteRule(rule) {
    return !isProtectRule(rule)
}

function isEpisodeOnly(rule) {
    return rule.Filters.MediaKinds.length === 1 && rule.Filters.MediaKinds[0] === 'Episode'
}

function isAllMedia(kinds) {
    return kinds.length === mediaKinds.length && mediaKinds.every(kind => kinds.includes(kind))
}

function mediaKindValue(kinds) {
    return isAllMedia(kinds) ? allMediaKind : kinds[0]
}

function normalizeMediaKinds(kinds) {
    const selected = Array.isArray(kinds) ? unique(kinds.filter(kind => mediaKinds.includes(kind))) : []
    if (isAllMedia(selected)) {
        return mediaKinds.slice()
    }

    return selected.length > 0 ? selected : ['Movie']
}

function expandMediaKinds(rule) {
    const kinds = normalizeMediaKinds(rule && rule.Filters && rule.Filters.MediaKinds)
    if (kinds.length <= 1 || isAllMedia(kinds)) {
        return [normalizeRule(rule)]
    }

    const baseId = rule.Id || createId()
    return kinds.map(kind => {
        const split = JSON.parse(JSON.stringify(rule))
        split.Id = `${baseId}-${kind.toLowerCase()}`
        split.Filters = split.Filters || {}
        split.Filters.MediaKinds = [kind]
        return normalizeRule(split)
    })
}

function mapFavoriteFilter(value) {
    if (value === 'AnyUser') return 'NotFavoriteByAnyUser'
    if (value === 'AllUsers') return 'NotFavoriteByAllUsers'
    return 'Ignore'
}

function selectHtml(field, label, value, values) {
    return `<div class="selectContainer"><label class="selectLabel">${label}</label><select is="emby-select" data-field="${field}" class="emby-select-withcolor emby-select">${values.map(item => `<option value="${item}" ${item === value ? 'selected="selected"' : ''}>${optionLabel(field, item)}</option>`).join('')}</select><div class="selectArrowContainer"><div style="visibility:hidden;display:none;">0</div><span class="selectArrow material-icons keyboard_arrow_down" aria-hidden="true"></span></div></div>`
}

function numberHtml(field, label, value, min) {
    return `<div class="inputContainer"><label class="inputLabel inputLabelUnfocused">${label}</label><input is="emby-input" type="number" min="${min}" data-field="${field}" value="${escapeAttribute(value)}" /></div>`
}

function textListHtml(field, label, values, description) {
    return `<div class="inputContainer"><label class="inputLabel inputLabelUnfocused">${label}</label><input is="emby-input" type="text" data-field="${field}" value="${escapeAttribute((values || []).join(', '))}" />${description ? `<div class="fieldDescription">${escapeHtml(description)}</div>` : ''}</div>`
}
function textareaHtml(field, label, values) {
    return `<div class="inputContainer"><label class="inputLabel inputLabelUnfocused">${label}</label><textarea is="emby-textarea" data-field="${field}" rows="2">${escapeHtml((values || []).join('\n'))}</textarea></div>`
}

function userListHtml(field, users, selectedIds, label) {
    if (!users || users.length === 0) {
        return textareaHtml(field, label, selectedIds)
    }

    const selected = selectedIds.map(id => id.toLowerCase())
    return listHtml(field, label, users.map(user => ({ value: user.Id, label: user.Name })), selected)
}

function locationListHtml(locations, selectedLocations) {
    if (!locations || locations.length === 0) {
        return textareaHtml('Locations', 'Locations', selectedLocations)
    }

    const selected = selectedLocations.map(location => location.toLowerCase())
    return listHtml('Locations', 'Locations', locations.map(location => ({ value: location, label: location })), selected)
}

function listHtml(field, label, items, selectedLowerValues) {
    return `
        <div class="mediaCleanerListBlock">
            <h2 class="checkboxListLabel mediaCleanerListLabel">${label}</h2>
            <div class="paperList checkboxList checkboxList-paperList mediaCleanerCheckList" data-field="${field}">
                ${items.map(item => listItemHtml(item, selectedLowerValues.includes(String(item.value).toLowerCase()))).join('')}
            </div>
        </div>`
}

function listItemHtml(item, checked) {
    return `<label><input is="emby-checkbox" type="checkbox" data-mini="true" data-value="${escapeAttribute(item.value)}" ${checked ? 'checked="checked"' : ''} /><span>${escapeHtml(item.label)}</span></label>`
}

function checkboxHtml(value, checked) {
    return `<label><input is="emby-checkbox" type="checkbox" value="${value}" ${checked ? 'checked="checked"' : ''} /><span>${labelFor(value)}</span></label>`
}


function menuCommandButton(command, icon, label, disabled = false) {
    const disabledAttribute = disabled ? ' disabled="disabled" aria-disabled="true"' : ''
    return `<button is="emby-button" type="button" class="listItem listItem-button actionSheetMenuItem emby-button" data-command="${command}" role="menuitem"${disabledAttribute}><span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ${icon}" aria-hidden="true"></span><div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText">${escapeHtml(label)}</div></div></button>`
}

function iconButton(command, icon, label, options = {}) {
    const condition = options.condition ? ` data-condition="${escapeAttribute(options.condition)}"` : ''
    const className = options.className ? ` ${options.className}` : ''
    return `<button is="paper-icon-button-light" type="button" class="mediaCleanerIconButton${className}" data-command="${command}"${condition} title="${escapeAttribute(label)}" aria-label="${escapeAttribute(label)}"><span class="material-icons ${icon}" aria-hidden="true"></span></button>`
}

function unique(values) {
    return values.filter((value, index) => values.indexOf(value) === index)
}


function optionLabel(field, value) {
    const labels = {
        TriggerKind: { Played: 'Played before cutoff', NotPlayed: 'Not played since added', AddedAge: 'Age since added (ignore playback)' },
        MediaKind: { All: 'All media' },
        PlayedKeepKind: { AnyUser: 'At least one user', AnyUserRolling: 'Most recent play by any user', AllUsers: 'Every user' },
        UsersMode: { Acknowledge: 'Selected users only', Ignore: 'All users except selected' },
        FavoriteUsersMode: { Acknowledge: 'Selected users only', Ignore: 'All users except selected' },
        FavoriteUsersScope: { AllUsers: 'All users', SelectedOnly: 'Selected users only', AllExceptSelected: 'All users except selected' },
        PlaybackUsersScope: { AllUsers: 'All users', SelectedOnly: 'Selected users only', AllExceptSelected: 'All users except selected' },
        FavoriteFilter: {
            FavoriteByAnyUser: 'Favorited by at least one user',
            FavoriteByAllUsers: 'Favorited by every user',
            NotFavoriteByAnyUser: 'Not favorited by any user',
            NotFavoriteByAllUsers: 'Not favorited by at least one user',
        },
        LocationsMode: { Include: 'Only selected locations', Exclude: 'All locations except selected' },
        TagFilterMode: { Inclusion: 'Require any listed tag', Exclusion: 'Exclude any listed tag' },
        DeleteEpisodes: { Episode: 'Matching episodes individually', Season: 'Complete seasons', Series: 'Complete series', SeriesEnded: 'Complete ended series' },
        KeepSeriesKind: { None: 'No exception', First: 'Keep the first', Last: 'Keep the latest' },
    }

    return labels[field] && labels[field][value] ? labels[field][value] : labelFor(value)
}

function labelFor(value) {
    if (value === 'AudioBook') return 'Audiobook'
    return String(value).replace(/([a-z])([A-Z])/g, '$1 $2')
}

function lines(value) {
    return String(value || '').split(/\r?\n|,/).map(x => x.trim()).filter(Boolean)
}

function numberValue(value, fallback) {
    const parsed = Number(value)
    return Number.isFinite(parsed) ? parsed : fallback
}

function createId() {
    if (typeof crypto !== 'undefined' && crypto.randomUUID) {
        return crypto.randomUUID().replace(/-/g, '')
    }

    return Math.random().toString(16).slice(2) + Date.now().toString(16)
}

function cssEscape(value) {
    if (typeof CSS !== 'undefined' && CSS.escape) {
        return CSS.escape(value)
    }

    return String(value).replace(/"/g, '\\"')
}

function prefersReducedMotion() {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

function escapeHtml(value) {
    return String(value).replace(/[&<>"]/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[ch]))
}

function escapeAttribute(value) {
    return escapeHtml(value).replace(/'/g, '&#39;')
}
