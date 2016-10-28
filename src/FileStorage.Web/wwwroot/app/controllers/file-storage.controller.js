﻿(function () {
  'use strict';

  angular
      .module('app')
      .controller('fileStorageController', fileStorageController);

  fileStorageController.$inject = ['folderService', 'Alertify', 'FileUploader', '$uibModal', '$scope', 'Session'];

  function fileStorageController(folderService, Alertify, FileUploader, $uibModal, $scope, Session) {
    var vm = this;
    var modal = null;
    vm.changeFolder = changeFolder;
    vm.currentFolder = {};
    vm.workPlaceItems = {
      filesAndFolders: []
    };
    activate();


    vm.uploader = new FileUploader({
      headers: { "Authorization": Session.accessToken },
      url: '/api/files/',
      removeAfterUpload: true
    });
  
    vm.uploader.onAfterAddingFile = function (item) {
      item.url = '/api/files/?directoryUniqId=' + vm.workPlaceItems.uniqueFolderId;
      modal = openProgressModal(item);
    };

    vm.uploader.onSuccessItem = function () {
      setTimeout(function () {
        modal.close();
        $scope.progress = 0;
      }, 500);
      changeFolder(vm.workPlaceItems.uniqueFolderId);
    };

    vm.uploader.onProgressItem = function (item, progress) {
      $scope.progress = progress;
    };
    vm.uploader.onErrorItem = function (item, response) {
      setTimeout(function () {
        modal.close();
        $scope.progress = 0;
      }, 500);
      Alertify.error(response);
    };

    function activate() {
      return folderService.getAllFolders().then(function (response) {
        vm.workPlaceItems = response;
        vm.workPlaceItems.filesAndFolders = response.folders.concat(response.files);
      }).catch(function (err) {
        Alertify.error(err.data);
      });
    }

    function changeFolder(folderId) {
      return folderService.getFolder(folderId)
        .then(function (response) {
          vm.workPlaceItems = response;
          vm.workPlaceItems.filesAndFolders = response.folders.concat(response.files);
        }).catch(function (err) {
          Alertify.error(err.data);
        });
    }

    function openProgressModal(item) {
      var modal = $uibModal.open({
        animation: true,
        templateUrl: '/app/views/uploadFile.html',
        scope: $scope,
        backdrop: 'static'
      });

      modal.opened.then(function () {
        item.upload();
      });

      return modal;
    };
  }
})();
