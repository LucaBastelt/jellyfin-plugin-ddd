addEventListener("hashchange", (event) => {

    console.log("The hash has changed!");
    checkDddUpdateNeeded();
})
navigation.addEventListener("navigate", e => {
    console.log("The URL has changed!");
    checkDddUpdateNeeded();
});
window.addEventListener('popstate', function (event) {
    console.log("NavState has changed!");
    checkDddUpdateNeeded();
});
function checkDddUpdateNeeded(){
    var path = location.hash.split('?');
    var queryParams = (path[1] ?? "").split('&');
    console.log(path);

    var id = queryParams.find(q => q.startsWith('id='))?.replace('id=', '');

    if (path[0] === '#/details' && id) {
        var request = new XMLHttpRequest();
        request.open("POST", "/plugin/ddd/" + id);

        request.addEventListener('load', function (event) {
            if (request.status >= 200 && request.status < 300) {
                var responseJson = JSON.parse(request.responseText);
                console.log(responseJson);
                patchDescription(responseJson);

                loadDddUrl(id);
            } else {
                console.warn(request.statusText, request.responseText);
            }
        });

        request.send()
    }
}

function loadDddUrl(id){
    var request = new XMLHttpRequest();
    request.open("POST", "/plugin/ddd/" + id + "/dddUrl");

    request.addEventListener('load', function (event) {
        if (request.status >= 200 && request.status < 300) {

            var element = document.querySelector('.itemTags');
            var a = document.createElement('a');
            element.parentElement.insertBefore(a, element);
            a.href = JSON.parse(request.responseText);
            a.innerText = "DoesTheDogDie Website";
            a.target = '_blank';
        } else {
            console.warn(request.statusText, request.responseText);
        }
    });

    request.send()
}

function patchDescription(content) {
    var element = document.querySelector('.itemTags');
    var p = document.createElement('p');
    element.parentElement.insertBefore(p, element);

    p.innerHTML = "<b>Content Warnings: </b>";

    for (const e of content) {
        p.innerHTML += '<span title="' + e.Comment + '">' + e.Name + '</span>, ';
    }
    p.innerHTML = p.innerHTML.slice(0, p.innerHTML.length - 2);
}

checkDddUpdateNeeded();
