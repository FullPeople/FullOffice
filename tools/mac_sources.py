def core_sources(root):
    return [str(root/'src/FullOffice.Mac'/name) for name in ['TextCodec.swift','OfficeFileSession.swift','OfficeCoordinator.swift','OfficeRecovery.swift']]

def full_sources(root):
    return core_sources(root)+[str(root/'src/FullOffice.Mac'/name) for name in ['MacUpdateManifest.swift','MacUpdateFeed.swift','MacUpdateDownload.swift','MacUpdateController.swift','MarkdownPreviewAPI.swift','TextDocumentWindow.swift','TextUIEvidence.swift','ProductProbe.swift','main.swift']]
