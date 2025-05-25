export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' });

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this));
    });

    view.querySelector('#clearPreviewBtn').addEventListener('click', function (e) {
        import(commonsUrl).then(clearPreviewList.bind(this));
    });
}

function onViewShow(commons) {
    const page = this;
    commons.setTabs('MediaCleaner', commons.TabPreview, commons.getTabs);
    Dashboard.showLoadingMsg();

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        const $previewList = page.querySelector('#previewList');
        const $previewEmptyMessage = page.querySelector('#previewEmptyMessage');
        const $clearPreviewBtnContainer = page.querySelector('#clearPreviewBtnContainer');
        const $previewModeStatus = page.querySelector('#previewModeStatus');

        if (config.PreviewMode) {
            $previewModeStatus.innerHTML = '<span style="color: #4CAF50; font-weight: bold;">[ENABLED]</span>';
        } else {
            $previewModeStatus.innerHTML = '<span style="color: #F44336; font-weight: bold;">[DISABLED]</span>';
        }
        
        if (!config.PreviewItems || config.PreviewItems.length === 0) {
            $previewList.innerHTML = '';
            $previewEmptyMessage.style.display = 'block';
            $clearPreviewBtnContainer.style.display = 'none';
        } else {
            $previewEmptyMessage.style.display = 'none';
            $clearPreviewBtnContainer.style.display = 'block';
            renderPreviewList($previewList, config.PreviewItems);
        }
        
        Dashboard.hideLoadingMsg();
    });
}

function renderPreviewList($container, items) {
    let html = '<div class="paperList">';
    
    items.forEach(item => {
        const date = new Date(item.PreviewDate).toLocaleString();
        
        html += `
        <div class="listItem">
            <div class="listItemBody">
                <div class="listItemBodyText">
                    <div class="secondary listItemBodyText-secondary" style="margin-bottom: 0.5em;">
                        <span style="font-weight: bold;">${item.Type}</span> - ${date}
                    </div>
                    <h3 class="listItemBodyText-primary" style="margin: 0;">${item.Name}</h3>
                    <div class="secondary listItemBodyText-secondary" style="margin-top: 0.5em;">
                        Reason: ${item.Reason}
                    </div>
                    <div class="secondary listItemBodyText-secondary">
                        Path: ${item.Path}
                    </div>
                </div>
            </div>
        </div>
        `;
    });
    
    html += '</div>';
    $container.innerHTML = html;
}

function clearPreviewList(commons) {
    Dashboard.showLoadingMsg();

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        config.PreviewItems = [];

        ApiClient.updatePluginConfiguration(commons.pluginId, config).then(result => {
            Dashboard.processPluginConfigurationUpdateResult(result);
            Dashboard.navigate(window.location.reload());
        });
    });
}
