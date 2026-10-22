(function () {
  return {
    hasApp: !!window.app,
    hasSimulator: !!window.app?.simulator,
    url: location.href,
    ready: document.readyState,
  };
})()